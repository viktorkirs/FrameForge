using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FrameForge.Core;
using FrameForge.Diagnostics;
using FrameForge.Encoding;
using Microsoft.Win32;

namespace FrameForge.UI;

public partial class VideoEditorWindow : Window
{
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(20) };
    readonly CancellationTokenSource _lifetime = new();
    CancellationTokenSource? _waveformCancellation, _exportCancellation;
    readonly string? _initialPath;
    string? _sourcePath;
    VideoEditorSource? _source;
    VideoEditTimeline? _timeline;
    VideoEditorPreview? _framePreview;
    VideoEditorFrameRequest? _pendingPreviewFrame;
    long _previewRevision;
    bool _previewWorkerRunning;
    VideoEditClip[]? _exportedClips;
    TimeSpan _position;
    int _activeClip;
    bool _playing, _mediaReady, _updatingPosition, _loading, _exporting, _closed, _closeAfterExport;
    Task _exportTask = Task.CompletedTask;
    Dictionary<long, ImageSource> _thumbnails = new();
    HashSet<long> _pendingThumbnails = new();

    public VideoEditorWindow(string? path = null)
    {
        InitializeComponent();
        _initialPath = path;
        _timer.Tick += Playback_Tick;
        TimelineCanvas.InteractionStarted += Pause;
        TimelineCanvas.InteractionCompleted += () => Seek(_position);
        TimelineCanvas.TrimPreviewRequested += PreviewTrimFrame;
        TimelineCanvas.SeekRequested += Seek;
        TimelineCanvas.SelectionChanged += RefreshSelection;
        TimelineCanvas.Edited += position => AfterEdit(previewPosition: position);
        RefreshControls();
    }

    async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_initialPath != null) await LoadAsync(_initialPath);
        else
        {
            PreviewMessage.Text = Loc.Get("EditorOpen");
            PickSource();
        }
    }

    async Task LoadAsync(string path)
    {
        if (_loading || _exporting) return;
        Pause();
        _loading = true;
        RefreshControls();
        StatusText.Text = Loc.Get("EditorLoading");
        try
        {
            VideoEditorPreview framePreview = await VideoEditorMedia.OpenPreviewAsync(path);
            VideoEditorSource source = framePreview.Source;
            if (_closed) return;
            var timeline = new VideoEditTimeline(source.Duration);
            _waveformCancellation?.Cancel();
            _waveformCancellation?.Dispose();
            _waveformCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            Player.Close();
            _mediaReady = false;
            _sourcePath = Path.GetFullPath(path);
            _source = source;
            _framePreview = framePreview;
            _previewRevision++;
            _pendingPreviewFrame = null;
            PausedFrame.Source = null;
            PausedFrame.Visibility = Visibility.Collapsed;
            _timeline = timeline;
            _exportedClips = null;
            _position = TimeSpan.Zero;
            _activeClip = 0;
            TimelineCanvas.Timeline = timeline;
            TimelineCanvas.Waveform = null;
            _thumbnails = new();
            _pendingThumbnails = new();
            TimelineCanvas.Thumbnails = _thumbnails;
            FileLabel.Text = $"{Path.GetFileName(path)}  ·  {source.Width} × {source.Height}  ·  {source.FrameRate:0.##} FPS";
            FileLabel.ToolTip = _sourcePath;
            PreviewMessage.Text = Loc.Get("EditorLoading");
            PreviewMessage.Visibility = Visibility.Visible;
            Player.Source = new Uri(_sourcePath);
            // Manual mode still needs Play/Pause to initialize the video surface.
            Player.Play();
            Player.Pause();
            ZoomSlider.Value = 1;
            StatusText.Text = source.HasAudio ? Loc.Get("EditorWaveform") : Loc.Get("EditorNoAudio");
            RefreshSelection();
            UpdatePosition(TimeSpan.Zero);
            UpdateExtent();
            _ = ReadWaveformAsync(_sourcePath, source, _waveformCancellation.Token);
            _ = ReadThumbnailsAsync();
            RequestPausedFrame(new(TimeSpan.Zero, TimeSpan.Zero, source.Duration));
        }
        catch (Exception ex)
        {
            Log.Error("Opening video editor source.", ex);
            if (!_closed) StatusText.Text = ex.Message;
        }
        finally
        {
            _loading = false;
            if (!_closed) RefreshControls();
        }
    }

    async Task ReadWaveformAsync(string path, VideoEditorSource source, CancellationToken cancellationToken)
    {
        if (!source.HasAudio) { TimelineCanvas.Waveform = new(false, []); return; }
        try
        {
            AudioWaveform waveform = await Task.Run(() => AudioWaveform.Read(path, source.Duration, cancellationToken), cancellationToken);
            if (cancellationToken.IsCancellationRequested || _closed) return;
            TimelineCanvas.Waveform = waveform;
            TimelineCanvas.InvalidateVisual();
            if (StatusText.Text == Loc.Get("EditorWaveform")) StatusText.Text = "";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error("Reading editor waveform.", ex);
            if (!cancellationToken.IsCancellationRequested && !_closed && !_exporting) StatusText.Text = Loc.Get("EditorWaveformFailed");
        }
    }

    async Task ReadThumbnailsAsync()
    {
        if (_timeline == null || _sourcePath == null || _waveformCancellation == null) return;
        string path = _sourcePath;
        CancellationToken token = _waveformCancellation.Token;
        Dictionary<long, ImageSource> thumbnails = _thumbnails;
        HashSet<long> pending = _pendingThumbnails;
        foreach (TimeSpan start in _timeline.Clips.Select(clip => clip.Start).Distinct().ToArray())
        {
            if (token.IsCancellationRequested || _closed) return;
            if (thumbnails.ContainsKey(start.Ticks) || !pending.Add(start.Ticks)) continue;
            try
            {
                ImageSource thumbnail = await VideoEditorMedia.ReadThumbnailAsync(path, start);
                if (token.IsCancellationRequested || _closed) return;
                thumbnails[start.Ticks] = thumbnail;
                TimelineCanvas.InvalidateVisual();
            }
            catch (Exception ex) { Log.Error("Reading editor thumbnail.", ex); }
            finally { pending.Remove(start.Ticks); }
        }
    }

    void Player_Opened(object sender, RoutedEventArgs e)
    {
        _mediaReady = true;
        PreviewMessage.Visibility = Visibility.Collapsed;
        Seek(_position);
        RefreshControls();
        _ = ReadThumbnailsAsync();
    }

    void Player_Failed(object sender, ExceptionRoutedEventArgs e)
    {
        Pause();
        _mediaReady = false;
        PreviewMessage.Text = Loc.Format("EditorPlaybackFailed", e.ErrorException.Message);
        PreviewMessage.Visibility = Visibility.Visible;
        RefreshControls();
    }

    void Player_Ended(object sender, RoutedEventArgs e)
    {
        if (!_playing || _timeline == null) return;
        AdvanceClip();
    }

    void AdvanceClip()
    {
        if (_timeline == null) return;
        if (_activeClip + 1 < _timeline.Clips.Count)
        {
            Seek(_timeline.StartOf(_activeClip + 1));
            Player.Play();
        }
        else
        {
            Pause();
            Seek(_timeline.Duration);
        }
    }

    void Playback_Tick(object? sender, EventArgs e)
    {
        if (!_playing || _timeline == null || _activeClip >= _timeline.Clips.Count) return;
        VideoEditClip clip = _timeline.Clips[_activeClip];
        TimeSpan sourcePosition = Player.Position;
        if (sourcePosition >= clip.End) { AdvanceClip(); return; }
        UpdatePosition(_timeline.StartOf(_activeClip) + TimeSpan.FromTicks(Math.Clamp((sourcePosition - clip.Start).Ticks, 0, clip.Duration.Ticks)));
        double x = 12 + _position.TotalSeconds * TimelineCanvas.PixelsPerSecond;
        if (x > TimelineScroll.HorizontalOffset + TimelineScroll.ViewportWidth - 20 || x < TimelineScroll.HorizontalOffset)
            TimelineScroll.ScrollToHorizontalOffset(Math.Max(0, x - 30));
    }

    void Seek(TimeSpan position)
    {
        if (_timeline == null || _timeline.Resolve(position) is not { } resolved) return;
        _activeClip = resolved.ClipIndex;
        if (_mediaReady)
        {
            Player.Position = resolved.SourceTime;
            if (_playing) Player.Play();
        }
        UpdatePosition(position);
        if (!_playing) RequestPlayheadFrame();
    }

    void PreviewTrimFrame(TimeSpan sourceTime, TimeSpan start, TimeSpan end)
    {
        if (_playing || _source == null) return;
        // During an edge drag, the pending source bounds are not in the montage yet.
        // Decode the dragged boundary directly instead of resolving it in the old timeline.
        RequestPausedFrame(new(sourceTime, start, end));
    }

    void RequestPlayheadFrame()
    {
        if (_timeline?.Resolve(_position) is not { } resolved) return;
        VideoEditClip clip = _timeline.Clips[resolved.ClipIndex];
        RequestPausedFrame(new(resolved.SourceTime, clip.Start, clip.End));
    }

    void RequestPausedFrame(VideoEditorFrameRequest request)
    {
        if (_closed || _playing || _framePreview == null) return;
        _pendingPreviewFrame = request;
        _previewRevision++;
        if (!_previewWorkerRunning) _ = ReadPausedFramesAsync();
    }

    async Task ReadPausedFramesAsync()
    {
        _previewWorkerRunning = true;
        // Coalesce synchronous slider, layout and trim updates into a single frame request.
        await Dispatcher.Yield(DispatcherPriority.Background);
        try
        {
            while (!_closed && !_playing && _pendingPreviewFrame is { } request && _framePreview is { } preview)
            {
                _pendingPreviewFrame = null;
                long revision = _previewRevision;
                try
                {
                    ImageSource frame = await preview.ReadFrameAsync(request, _lifetime.Token);
                    if (_closed || _playing || revision != _previewRevision) continue;
                    PausedFrame.Source = frame;
                    PausedFrame.Visibility = Visibility.Visible;
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Log.Error("Decoding paused editor frame.", ex);
                    if (!_closed && revision == _previewRevision)
                    {
                        // Keep the live player available if a particular source cannot provide still frames.
                        PausedFrame.Visibility = Visibility.Collapsed;
                        if (_mediaReady) Player.Position = request.Position;
                    }
                }
            }
        }
        finally { _previewWorkerRunning = false; }
    }

    void UpdatePosition(TimeSpan position)
    {
        _position = TimeSpan.FromTicks(Math.Clamp(position.Ticks, 0, _timeline?.Duration.Ticks ?? 0));
        _updatingPosition = true;
        SeekSlider.Maximum = Math.Max(.001, _timeline?.Duration.TotalSeconds ?? 0);
        SeekSlider.Value = _position.TotalSeconds;
        _updatingPosition = false;
        TimelineCanvas.Position = _position;
        TimeLabel.Text = $"{VideoTimelineControl.Format(_position, true)} / {VideoTimelineControl.Format(_timeline?.Duration ?? TimeSpan.Zero, true)}";
        if (_timeline?.Resolve(_position) is { } resolved)
        {
            VideoEditClip clip = _timeline.Clips[resolved.ClipIndex];
            SplitButton.IsEnabled = !_exporting && !_loading && resolved.SourceTime - clip.Start >= VideoEditTimeline.MinimumDuration
                && clip.End - resolved.SourceTime >= VideoEditTimeline.MinimumDuration;
        }
        else SplitButton.IsEnabled = false;
    }

    void TogglePlay()
    {
        if (_exporting || _loading || !_mediaReady || _timeline?.Clips.Count is not > 0) return;
        if (_playing) { Pause(); return; }
        if (_position >= _timeline.Duration) Seek(TimeSpan.Zero);
        _playing = true;
        _previewRevision++;
        _pendingPreviewFrame = null;
        PausedFrame.Visibility = Visibility.Collapsed;
        // A trim preview can show a boundary without moving the logical playhead.
        Seek(_position);
        PlayButton.Content = "Ⅱ";
        Player.Play();
        _timer.Start();
    }

    void Pause()
    {
        _playing = false;
        _timer.Stop();
        Player.Pause();
        PlayButton.Content = "▶";
        RequestPlayheadFrame();
    }

    void AfterEdit(bool selectStart = false, TimeSpan? previewPosition = null)
    {
        Pause();
        if (_timeline == null) return;
        RefreshSelection();
        UpdateExtent();
        if (_timeline.Clips.Count > 0)
        {
            PreviewMessage.Visibility = _mediaReady ? Visibility.Collapsed : Visibility.Visible;
            Seek(previewPosition ?? (selectStart ? _timeline.StartOf(_timeline.SelectedIndex) : _position));
            StatusText.Text = "";
        }
        else
        {
            _previewRevision++;
            _pendingPreviewFrame = null;
            PausedFrame.Source = null;
            PausedFrame.Visibility = Visibility.Collapsed;
            Player.Position = TimeSpan.Zero;
            UpdatePosition(TimeSpan.Zero);
            PreviewMessage.Text = Loc.Get("EditorEmpty");
            PreviewMessage.Visibility = Visibility.Visible;
            StatusText.Text = Loc.Get("EditorEmpty");
        }
        RefreshControls();
        _ = ReadThumbnailsAsync();
    }

    void RefreshSelection()
    {
        if (_timeline != null && _timeline.SelectedIndex >= 0 && _timeline.SelectedIndex < _timeline.Clips.Count)
        {
            VideoEditClip clip = _timeline.Clips[_timeline.SelectedIndex];
            TrimInBox.Text = VideoTimelineControl.Format(clip.Start, true);
            TrimOutBox.Text = VideoTimelineControl.Format(clip.End, true);
        }
        else TrimInBox.Text = TrimOutBox.Text = "";
        TimelineCanvas.InvalidateVisual();
        RefreshControls();
    }

    void RefreshControls()
    {
        bool available = !_loading && !_exporting;
        bool hasClips = _timeline?.Clips.Count > 0;
        bool selected = hasClips && _timeline!.SelectedIndex >= 0;
        OpenButton.IsEnabled = available;
        ExportButton.IsEnabled = available && hasClips;
        Transport.IsEnabled = available && hasClips && _mediaReady;
        EditTools.IsEnabled = available;
        TimelineCanvas.IsEnabled = available;
        TrimTools.IsEnabled = available && selected;
        DeleteButton.IsEnabled = selected;
        MoveLeftButton.IsEnabled = selected && _timeline!.SelectedIndex > 0;
        MoveRightButton.IsEnabled = selected && _timeline!.SelectedIndex < _timeline.Clips.Count - 1;
        UndoButton.IsEnabled = _timeline?.CanUndo == true;
        RedoButton.IsEnabled = _timeline?.CanRedo == true;
        ZoomSlider.IsEnabled = available && hasClips;
        UpdatePosition(_position);
    }

    void UpdateExtent()
    {
        TimelineCanvas.UpdateExtent(Math.Max(300, TimelineScroll.ViewportWidth), ZoomSlider.Value);
    }

    bool HasUnexportedChanges() => _timeline is { HasChanges: true } &&
        (_exportedClips == null || !_timeline.Clips.SequenceEqual(_exportedClips));

    bool ConfirmDiscard() => !HasUnexportedChanges() || MessageBox.Show(this, Loc.Get("EditorDiscard"), Loc.Get("EditorTitle"),
        MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    async void PickSource()
    {
        if (_loading || _exporting) return;
        Pause();
        var dialog = new OpenFileDialog { Filter = "MP4 (*.mp4)|*.mp4", CheckFileExists = true, Title = Loc.Get("EditorOpen") };
        if (dialog.ShowDialog(this) == true && ConfirmDiscard()) await LoadAsync(dialog.FileName);
    }

    async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_exporting || _loading || _timeline?.Clips.Count is not > 0 || _sourcePath == null) return;
        Pause();
        var dialog = new SaveFileDialog
        {
            Filter = "MP4 (*.mp4)|*.mp4", DefaultExt = ".mp4", AddExtension = true, OverwritePrompt = true,
            InitialDirectory = Path.GetDirectoryName(_sourcePath), FileName = Path.GetFileNameWithoutExtension(_sourcePath) + "-edited.mp4",
            Title = Loc.Get("EditorExport")
        };
        if (dialog.ShowDialog(this) != true) return;
        _exportTask = ExportAsync(dialog.FileName);
        await _exportTask;
    }

    async Task ExportAsync(string destination)
    {
        if (_timeline == null || _sourcePath == null) return;
        _exporting = true;
        _exportCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        VideoEditClip[] snapshot = _timeline.Clips.ToArray();
        ExportProgress.Value = 0;
        ExportProgress.Visibility = CancelExportButton.Visibility = Visibility.Visible;
        StatusText.Text = Loc.Format("EditorExporting", 0);
        RefreshControls();
        try
        {
            var progress = new Progress<double>(value =>
            {
                if (_closed || !_exporting) return;
                ExportProgress.Value = value;
                StatusText.Text = Loc.Format("EditorExporting", value);
            });
            await VideoEditorMedia.ExportAsync(_sourcePath, snapshot, destination, progress, _exportCancellation.Token);
            _exportedClips = snapshot;
            StatusText.Text = Loc.Format("EditorExported", destination);
        }
        catch (OperationCanceledException) { StatusText.Text = Loc.Get("EditorCanceled"); }
        catch (Exception ex)
        {
            Log.Error("Exporting edited video.", ex);
            StatusText.Text = ex.Message;
        }
        finally
        {
            _exporting = false;
            _exportCancellation.Dispose();
            _exportCancellation = null;
            ExportProgress.Visibility = CancelExportButton.Visibility = Visibility.Collapsed;
            RefreshControls();
        }
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        if (_exporting)
        {
            e.Cancel = true;
            base.OnClosing(e);
            if (_closeAfterExport) return;
            _closeAfterExport = true;
            _exportCancellation?.Cancel();
            await _exportTask;
            _closeAfterExport = false;
            Close();
            return;
        }
        if (!ConfirmDiscard()) e.Cancel = true;
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _previewRevision++;
        _pendingPreviewFrame = null;
        _framePreview = null;
        _lifetime.Cancel();
        _waveformCancellation?.Cancel();
        _timer.Stop();
        _timer.Tick -= Playback_Tick;
        Player.Close();
        _waveformCancellation?.Dispose();
        _lifetime.Dispose();
        base.OnClosed(e);
    }

    void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (_loading || _exporting || e.OriginalSource is TextBox) return;
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if (ctrl && e.Key == Key.B) Split_Click(this, e);
        else if (ctrl && e.Key == Key.Z && shift) Redo_Click(this, e);
        else if (ctrl && e.Key == Key.Z) Undo_Click(this, e);
        else if (ctrl && e.Key == Key.Y) Redo_Click(this, e);
        else if (e.Key == Key.Delete) Delete_Click(this, e);
        else if (e.Key == Key.Space) TogglePlay();
        else if (e.Key == Key.Home) { Pause(); Seek(TimeSpan.Zero); }
        else if (e.Key == Key.End) { Pause(); Seek(_timeline?.Duration ?? TimeSpan.Zero); }
        else if (e.Key is Key.Left or Key.Right)
        {
            Pause();
            Seek(_position + TimeSpan.FromSeconds((e.Key == Key.Left ? -1 : 1) * (shift ? 1 : 1 / Math.Max(1, _source?.FrameRate ?? 30))));
        }
        else return;
        e.Handled = true;
    }

    void Open_Click(object sender, RoutedEventArgs e) => PickSource();
    void Play_Click(object sender, RoutedEventArgs e) => TogglePlay();
    void Player_Click(object sender, MouseButtonEventArgs e) => TogglePlay();
    void Seek_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) { if (!_updatingPosition) { Pause(); Seek(TimeSpan.FromSeconds(e.NewValue)); } }
    void Zoom_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) { if (TimelineCanvas != null) UpdateExtent(); }
    void Timeline_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateExtent();
    void Timeline_ScrollChanged(object sender, ScrollChangedEventArgs e) { if (e.ViewportWidthChange != 0) UpdateExtent(); TimelineCanvas.InvalidateVisual(); }
    void Split_Click(object sender, RoutedEventArgs e) { if (_timeline?.Split(_position) == true) AfterEdit(); }
    void Delete_Click(object sender, RoutedEventArgs e) { if (_timeline?.DeleteSelected() == true) AfterEdit(selectStart: true); }
    void Undo_Click(object sender, RoutedEventArgs e) { if (_timeline?.Undo() == true) AfterEdit(); }
    void Redo_Click(object sender, RoutedEventArgs e) { if (_timeline?.Redo() == true) AfterEdit(); }
    void MoveLeft_Click(object sender, RoutedEventArgs e) { if (_timeline != null && _timeline.Move(_timeline.SelectedIndex, _timeline.SelectedIndex - 1)) AfterEdit(selectStart: true); }
    void MoveRight_Click(object sender, RoutedEventArgs e) { if (_timeline != null && _timeline.Move(_timeline.SelectedIndex, _timeline.SelectedIndex + 1)) AfterEdit(selectStart: true); }
    void CancelExport_Click(object sender, RoutedEventArgs e) => _exportCancellation?.Cancel();

    void Trim_Click(object sender, RoutedEventArgs e)
    {
        if (_timeline == null) return;
        if (!TimeSpan.TryParseExact(TrimInBox.Text, @"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture, out TimeSpan start) ||
            !TimeSpan.TryParseExact(TrimOutBox.Text, @"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture, out TimeSpan end) ||
            start < TimeSpan.Zero || end > _timeline.SourceDuration || end - start < VideoEditTimeline.MinimumDuration)
        {
            StatusText.Text = Loc.Get("EditorTrimInvalid");
            return;
        }
        if (_timeline.Trim(_timeline.SelectedIndex, start, end)) AfterEdit(selectStart: true);
    }
}
