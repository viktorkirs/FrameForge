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
    public event Action? RecordingRequested;
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
        InitializeTracks();
        InitializeVisualLayers();
        InitializeTimelineTools();
        Loc.Changed += EditorLanguageChanged;
        RefreshControls();
    }

    async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        WorkspaceNavigation.Visibility = RecordingRequested == null ? Visibility.Collapsed : Visibility.Visible;
        if (_initialPath != null)
        {
            if (string.Equals(Path.GetExtension(_initialPath), VideoEditProjectFile.Extension, StringComparison.OrdinalIgnoreCase)) await LoadProjectAsync(_initialPath);
            else await LoadAsync(_initialPath);
        }
        else
        {
            PreviewMessage.Text = Loc.Get("EditorDropHint");
        }
    }

    public void SuspendAndHide() { Pause(); Hide(); }
    public async void OpenDocument(string path)
    {
        if (_loading || _exporting) return;
        Pause(); if (!ConfirmDiscard()) return;
        if (string.Equals(Path.GetExtension(path), VideoEditProjectFile.Extension, StringComparison.OrdinalIgnoreCase)) await LoadProjectAsync(path);
        else await LoadAsync(path);
    }
    public async Task<bool> TryCloseProjectAsync()
    {
        Pause();
        if (_exporting) { _exportCancellation?.Cancel(); await _exportTask; }
        Close(); return _closed;
    }
    void RecordingWorkspace_Click(object sender, RoutedEventArgs e) => RecordingRequested?.Invoke();

    async Task LoadAsync(string path)
    {
        if (_loading || _exporting) return;
        Pause();
        _frameMode = "move";
        FinishCropButton.Visibility = Visibility.Collapsed;
        _loading = true;
        RefreshControls();
        StatusText.Text = Loc.Get("EditorLoading");
        try
        {
            VideoEditorPreview framePreview = await VideoEditorMedia.OpenPreviewAsync(path);
            VideoEditorSource source = framePreview.Source;
            if (_closed) return;
            var timeline = new VideoEditTimeline(source.Duration, Path.GetFullPath(path));
            _waveformCancellation?.Cancel();
            _waveformCancellation?.Dispose();
            _waveformCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            Player.Close();
            _mediaReady = false;
            _sourcePath = Path.GetFullPath(path);
            _source = source;
            _framePreview = framePreview;
            ResetAssets(_sourcePath, framePreview);
            _previewRevision++;
            _pendingPreviewFrame = null;
            PausedFrame.Source = null;
            PausedFrame.Visibility = Visibility.Collapsed;
            _timeline = timeline;
            ConfigureOutputFormats();
            _savedProjectState = null; _projectPath = null; _projectNeedsSave = false;
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
            _playerPath = _sourcePath;
            // Manual mode still needs Play/Pause to initialize the video surface.
            Player.Play();
            Player.Pause();
            ZoomSlider.Value = 1;
            StatusText.Text = source.HasAudio ? Loc.Get("EditorWaveform") : Loc.Get("EditorNoAudio");
            RefreshSelection();
            RefreshTracks();
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
            TimelineCanvas.Waveforms[path] = waveform;
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
        if (_timeline == null || _waveformCancellation == null) return;
        string? path = _sourcePath;
        CancellationToken token = _waveformCancellation.Token;
        Dictionary<long, ImageSource> thumbnails = _thumbnails;
        HashSet<long> pending = _pendingThumbnails;
        foreach (VideoEditClip clip in _timeline.Clips.Concat(_timeline.Tracks.Where(track => track.Kind == VideoEditTrackKind.Video).SelectMany(track => track.Clips)).ToArray())
        {
            TimeSpan start = clip.Start;
            if (clip.IsStill) { GetStillBitmap(clip); TimelineCanvas.InvalidateVisual(); continue; }
            string? clipPath = clip.SourcePath ?? path;
            if (clipPath == null) continue;
            if (token.IsCancellationRequested || _closed) return;
            if (TimelineCanvas.SourceThumbnails.ContainsKey((clipPath, start.Ticks))) continue;
            try
            {
                ImageSource thumbnail = await VideoEditorMedia.ReadThumbnailAsync(clipPath, start);
                if (token.IsCancellationRequested || _closed) return;
                if (clipPath == path) thumbnails[start.Ticks] = thumbnail;
                TimelineCanvas.SourceThumbnails[(clipPath, start.Ticks)] = thumbnail;
                TimelineCanvas.InvalidateVisual();
            }
            catch (Exception ex) { Log.Error("Reading editor thumbnail.", ex); }
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
        // The montage clock advances across trimmed intervals, different sources and empty gaps.
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
        if (!_playing || _timeline == null) return;
        TimeSpan position = _playStart + _playClock.Elapsed;
        UpdatePosition(position);
        if (_position >= _timeline.Duration) { Pause(); Seek(_timeline.Duration); return; }
        SyncBasePlayback();
        SyncTrackPlayback();
        PumpVoice();
        double x = 12 + _position.TotalSeconds * TimelineCanvas.PixelsPerSecond;
        if (x > TimelineScroll.HorizontalOffset + TimelineScroll.ViewportWidth - 20 || x < TimelineScroll.HorizontalOffset)
            TimelineScroll.ScrollToHorizontalOffset(Math.Max(0, x - 30));
    }

    void Seek(TimeSpan position)
    {
        if (_timeline == null) return;
        UpdatePosition(position);
        SyncTrackPlayback(force: true);
        if (_timeline.Resolve(_position) is not { } resolved)
        {
            _activeClip = -1; _previewRevision++; _pendingPreviewFrame = null;
            Player.Pause(); Player.Visibility = PausedFrame.Visibility = Visibility.Collapsed;
            UpdatePreviewLayout(); return;
        }
        _activeClip = resolved.ClipIndex;
        ActivateBaseSource(_timeline.Clips[_activeClip]);
        if (_mediaReady)
        {
            Player.Position = resolved.SourceTime;
            if (_playing) Player.Play();
        }
        UpdatePreviewLayout();
        if (!_playing) RequestPlayheadFrame();
    }

    void PreviewTrimFrame(TimeSpan sourceTime, TimeSpan start, TimeSpan end)
    {
        if (_playing || _source == null) return;
        if (_timeline?.SelectedTrack is { Kind: VideoEditTrackKind.Video } track && _timeline.SelectedClip is { } clip &&
            _trackPlayers.TryGetValue(track.Id, out TrackPreview? layer))
        {
            _ = ReadTrackFrameAsync(layer, clip with { Start = start, End = end }, sourceTime);
            return;
        }
        if (_timeline?.SelectedTrack?.Kind == VideoEditTrackKind.Audio) return;
        // During an edge drag, the pending source bounds are not in the montage yet.
        // Decode the dragged boundary directly instead of resolving it in the old timeline.
        RequestPausedFrame(new(sourceTime, start, end));
    }

    void RequestPlayheadFrame()
    {
        if (_timeline?.Resolve(_position) is not { } resolved)
        {
            _previewRevision++; _pendingPreviewFrame = null;
            Player.Pause(); Player.Visibility = PausedFrame.Visibility = Visibility.Collapsed;
            return;
        }
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
                    PausedFrame.Visibility = HasBaseFrame ? Visibility.Visible : Visibility.Collapsed;
                    UpdatePreviewLayout();
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
        if (_voiceRecorder == null && _timeline?.SelectedTrackId != null)
        {
            SplitButton.IsEnabled = !_exporting && !_loading && _timeline.SelectedClips.Any(clip => _position - clip.Position >= VideoEditTimeline.MinimumDuration && clip.Position + clip.Duration - _position >= VideoEditTimeline.MinimumDuration);
        }
        else if (_voiceRecorder == null && _timeline?.Resolve(_position) is { } resolved && _position < _timeline.MainDuration)
        {
            VideoEditClip clip = _timeline.Clips[resolved.ClipIndex];
            SplitButton.IsEnabled = !_exporting && !_loading && resolved.SourceTime - clip.Start >= VideoEditTimeline.MinimumDuration
                && clip.End - resolved.SourceTime >= VideoEditTimeline.MinimumDuration;
        }
        else SplitButton.IsEnabled = false;
    }

    void TogglePlay()
    {
        if (_exporting || _loading || _timeline == null || _timeline.Duration <= TimeSpan.Zero) return;
        if (_playing) { Pause(); return; }
        if (_position >= _timeline.Duration) Seek(TimeSpan.Zero);
        _playing = true;
        _playStart = _position;
        _playClock.Restart();
        _voiceRecorder?.Begin();
        _previewRevision++;
        _pendingPreviewFrame = null;
        PausedFrame.Visibility = Visibility.Collapsed;
        // A trim preview can show a boundary without moving the logical playhead.
        Seek(_position);
        PlayButton.Tag = FindResource("EditorIconPause");
        if (HasBaseFrame) Player.Play();
        SyncTrackPlayback(force: true);
        _timer.Start();
    }

    void Pause()
    {
        if (_playing && _timeline != null) UpdatePosition(_playStart + _playClock.Elapsed);
        _playing = false;
        _playClock.Stop();
        _timer.Stop();
        Player.Pause();
        PauseTrackPlayback();
        StopVoice();
        PlayButton.Tag = FindResource("EditorIconPlay");
        RequestPlayheadFrame();
    }

    void AfterEdit(bool selectStart = false, TimeSpan? previewPosition = null)
    {
        Pause();
        if (_timeline == null) return;
        RefreshSelection();
        RefreshTracks();
        UpdateExtent();
        if (_timeline.Duration > TimeSpan.Zero)
        {
            PreviewMessage.Visibility = Visibility.Collapsed;
            Seek(previewPosition ?? (selectStart ? _timeline.SelectedStart : _position));
            if (selectStart) ShowSelectedTrack();
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
        if (_timeline?.SelectedClip is { } clip)
        {
            TrimInBox.Text = VideoTimelineControl.Format(clip.Start, true);
            TrimOutBox.Text = VideoTimelineControl.Format(clip.End, true);
        }
        else TrimInBox.Text = TrimOutBox.Text = "";
        RefreshInspector();
        TimelineCanvas.InvalidateVisual();
        RefreshControls();
    }

    void RefreshControls()
    {
        bool available = !_loading && !_exporting && _voiceRecorder == null;
        bool hasClips = _timeline?.Duration > TimeSpan.Zero;
        bool selected = _timeline?.SelectedClip != null;
        OpenButton.IsEnabled = available;
        OpenProjectButton.IsEnabled = available;
        SaveProjectButton.IsEnabled = available && _timeline != null;
        ExportButton.IsEnabled = available && hasClips;
        Transport.IsEnabled = !_loading && !_exporting && hasClips;
        SeekSlider.IsEnabled = available && hasClips;
        EditTools.IsEnabled = available;
        ImportTools.IsEnabled = available;
        Inspector.IsEnabled = available && _timeline != null;
        VolumeSlider.IsEnabled = selected;
        VolumeTools.Visibility = _timeline?.SelectedClip?.IsStill == true ? Visibility.Collapsed : Visibility.Visible;
        FrameTools.IsEnabled = available && selected && _timeline!.SelectedTrack?.Kind != VideoEditTrackKind.Audio;
        RemoveTrackButton.IsEnabled = _timeline?.SelectedTrack != null;
        ApplyPositionButton.IsEnabled = selected;
        TrackPositionBox.IsEnabled = selected;
        TimelineCanvas.IsEnabled = available;
        TrackHeaders.IsEnabled = available;
        TrimTools.IsEnabled = available && selected;
        DeleteButton.IsEnabled = selected;
        MoveLeftButton.IsEnabled = selected && _timeline!.CanPlaceSelected(_timeline.SelectedStart - TimeSpan.FromSeconds(.1));
        MoveRightButton.IsEnabled = selected && _timeline!.CanPlaceSelected(_timeline.SelectedStart + TimeSpan.FromSeconds(.1));
        UndoButton.IsEnabled = _timeline?.CanUndo == true;
        RedoButton.IsEnabled = _timeline?.CanRedo == true;
        CopyTrackButton.IsEnabled = available && _timeline?.SelectedClips.Count > 0;
        PasteButton.IsEnabled = PasteTrackButton.IsEnabled = available;
        RefreshRangeTools();
        ZoomSlider.IsEnabled = available && hasClips;
        VoiceButton.IsEnabled = !_loading && !_exporting && hasClips;
        MicrophoneBox.IsEnabled = available;
        RefreshProjectTitle();
        UpdatePosition(_position);
    }

    void UpdateExtent()
    {
        TimelineCanvas.UpdateExtent(Math.Max(300, TimelineScroll.ViewportWidth), ZoomSlider.Value);
    }

    bool HasUnexportedChanges() => _projectNeedsSave || _timeline != null &&
        (_savedProjectState.HasValue ? _timeline.StateId != _savedProjectState : _timeline.HasChanges);

    bool ConfirmDiscard()
    {
        if (!HasUnexportedChanges()) return true;
        MessageBoxResult result = AppDialog.ConfirmSave(this);
        return result == MessageBoxResult.No || result == MessageBoxResult.Yes && SaveProject();
    }

    async void PickSource()
    {
        if (_loading || _exporting) return;
        Pause();
        var dialog = new OpenFileDialog
        {
            Filter = "FrameForge projects / video|*.ffproj;*.mp4;*.mov;*.m4v;*.wmv;*.avi|FrameForge (*.ffproj)|*.ffproj|MP4 (*.mp4)|*.mp4",
            CheckFileExists = true, Title = Loc.Get("EditorOpenProject")
        };
        if (ModalBackdrop.ShowDialog(dialog, this) == true && ConfirmDiscard())
        {
            if (string.Equals(Path.GetExtension(dialog.FileName), VideoEditProjectFile.Extension, StringComparison.OrdinalIgnoreCase)) await LoadProjectAsync(dialog.FileName);
            else await LoadAsync(dialog.FileName);
        }
    }

    async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_exporting || _loading || _timeline == null || _timeline.Duration <= TimeSpan.Zero || _sourcePath == null || _voiceRecorder != null) return;
        Pause();
        var dialog = new SaveFileDialog
        {
            Filter = "MP4 (*.mp4)|*.mp4", DefaultExt = ".mp4", AddExtension = true, OverwritePrompt = true,
            InitialDirectory = Path.GetDirectoryName(_sourcePath), FileName = Path.GetFileNameWithoutExtension(_sourcePath) + "-edited.mp4",
            Title = Loc.Get("EditorExport")
        };
        if (ModalBackdrop.ShowDialog(dialog, this) != true) return;
        _exportTask = ExportAsync(dialog.FileName);
        await _exportTask;
    }

    async Task ExportAsync(string destination)
    {
        if (_timeline == null || _sourcePath == null) return;
        _exporting = true;
        _exportCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        VideoEditSnapshot snapshot = _timeline.Snapshot();
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
            await VideoEditorMedia.ExportAsync(snapshot, destination, _source!.Width, _source.Height, _source.FrameRate, progress, _exportCancellation.Token);
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
        Pause();
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
        Loc.Changed -= EditorLanguageChanged;
        _previewRevision++;
        _pendingPreviewFrame = null;
        _framePreview = null;
        _lifetime.Cancel();
        _waveformCancellation?.Cancel();
        _timer.Stop();
        _timer.Tick -= Playback_Tick;
        Player.Close();
        CloseTrackPlayback();
        _waveformCancellation?.Dispose();
        _lifetime.Dispose();
        base.OnClosed(e);
    }

    void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (_reorderingTrack)
        {
            if (e.Key == Key.Escape)
            {
                if (Mouse.Captured is System.Windows.Controls.Primitives.Thumb grip) grip.CancelDrag();
                else FinishTrackReorder(canceled: true);
            }
            e.Handled = true; return;
        }
        if (_loading || _exporting) return;
        if (_voiceRecorder != null) { if (e.Key == Key.Space) { Pause(); e.Handled = true; } return; }
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if (ctrl && e.Key == Key.S) { SaveProject(shift); e.Handled = true; return; }
        if (ctrl && e.Key == Key.O) { OpenProject_Click(this, e); e.Handled = true; return; }
        if (e.OriginalSource is TextBox || e.OriginalSource is System.Windows.Controls.Primitives.Selector) return;
        if (ctrl && e.Key == Key.C) { if (shift) CopyTrack_Click(this, e); else Copy_Click(this, e); e.Handled = true; return; }
        if (ctrl && e.Key == Key.V) { if (shift) PasteTrack_Click(this, e); else Paste_Click(this, e); e.Handled = true; return; }
        if (ctrl && e.Key == Key.X) { Cut_Click(this, e); e.Handled = true; return; }
        if (e.Key == Key.Escape && TimelineCanvas.HasRange) { TimelineCanvas.ClearRange(); e.Handled = true; return; }
        if (e.Key == Key.Escape && _frameMode == "crop") { BeginFrameGesture("move"); e.Handled = true; return; }
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
    void Delete_Click(object sender, RoutedEventArgs e) { if (TimelineCanvas.HasRange) DeleteRange(copy: false); else if (_timeline?.DeleteSelected() == true) AfterEdit(selectStart: true); }
    void Undo_Click(object sender, RoutedEventArgs e) { if (_timeline?.Undo() == true) AfterEdit(); }
    void Redo_Click(object sender, RoutedEventArgs e) { if (_timeline?.Redo() == true) AfterEdit(); }
    void MoveLeft_Click(object sender, RoutedEventArgs e) { if (_timeline?.SetSelectedPosition(_timeline.SelectedStart - TimeSpan.FromSeconds(.1)) == true) AfterEdit(selectStart: true); }
    void MoveRight_Click(object sender, RoutedEventArgs e) { if (_timeline?.SetSelectedPosition(_timeline.SelectedStart + TimeSpan.FromSeconds(.1)) == true) AfterEdit(selectStart: true); }
    void CancelExport_Click(object sender, RoutedEventArgs e) => _exportCancellation?.Cancel();

    void Trim_Click(object sender, RoutedEventArgs e)
    {
        if (_timeline == null) return;
        if (!TimeSpan.TryParseExact(TrimInBox.Text, @"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture, out TimeSpan start) ||
            !TimeSpan.TryParseExact(TrimOutBox.Text, @"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture, out TimeSpan end) ||
            _timeline.SelectedClip is not { } clip || start < TimeSpan.Zero || end > _timeline.SourceLength(clip) || end - start < VideoEditTimeline.MinimumDuration)
        {
            StatusText.Text = Loc.Get("EditorTrimInvalid");
            return;
        }
        if (_timeline.Trim(_timeline.SelectedIndex, start, end)) AfterEdit(selectStart: true);
    }
}
