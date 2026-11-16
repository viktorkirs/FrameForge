using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using FrameForge.Capture;
using FrameForge.Core;
using FrameForge.Diagnostics;
using FrameForge.Encoding;
using Microsoft.Win32;

namespace FrameForge.UI;

public partial class VideoEditorWindow
{
    readonly Stopwatch _playClock = new();
    readonly Dictionary<string, VideoEditorPreview> _previews = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<Guid, TrackPreview> _trackPlayers = new();
    string? _playerPath;
    TimeSpan _playStart, _voiceStart;
    VoiceoverRecorder? _voiceRecorder;
    bool _refreshingInspector;
    string? _frameGesture;
    string _frameMode = "move";
    int _frameHandle = -1, _cropEdgeX, _cropEdgeY;
    Point? _framePress;
    VideoFrameTransform? _gestureTransform, _workingTransform;
    Matrix _gestureMatrix;
    Rect _gestureBounds;
    double _editorViewportHeight;
    bool HasBaseFrame => _timeline?.Resolve(_position) != null;

    sealed class TrackPreview(MediaElement player, Image still)
    {
        public MediaElement Player { get; } = player;
        public Image Still { get; } = still;
        public Guid? ClipId;
        public string? Path;
        public bool Ready;
        public long Revision;
        public CancellationTokenSource? FrameCancellation;
    }

    void InitializeTracks()
    {
        var microphones = new List<AudioDeviceInfo> { new("", Loc.Get("EditorDefaultMicrophone")) };
        microphones.AddRange(AudioDeviceCatalog.GetMicrophones());
        MicrophoneBox.ItemsSource = microphones;
        string? preferred = SettingsStore.Load().MicrophoneDeviceId;
        MicrophoneBox.SelectedItem = microphones.FirstOrDefault(device => device.Id == preferred) ?? microphones[0];
        // Commit a volume gesture as one undoable change instead of one state for every slider tick.
        VolumeSlider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) => CommitVolume()));
        VolumeSlider.PreviewMouseLeftButtonUp += (_, _) => CommitVolume();
        VolumeSlider.PreviewKeyUp += (_, _) => CommitVolume();
        FrameScaleSlider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) => CommitFrameScale()));
        FrameScaleSlider.PreviewMouseLeftButtonUp += (_, _) => CommitFrameScale();
        FrameScaleSlider.PreviewKeyUp += (_, _) => CommitFrameScale();
        InitializeFadeSliders();
    }

    void ResetAssets(string path, VideoEditorPreview preview)
    {
        CloseTrackPlayback();
        _previews.Clear();
        _previews.Add(path, preview);
        TimelineCanvas.SourceThumbnails.Clear();
        TimelineCanvas.Waveforms.Clear();
    }

    void ActivateBaseSource(VideoEditClip clip)
    {
        string? path = clip.SourcePath ?? _sourcePath;
        if (path == null) return;
        if (_previews.TryGetValue(path, out VideoEditorPreview? preview)) _framePreview = preview;
        Player.Volume = (_timeline?.MainMuted == true ? 0 : clip.Volume) * .8;
        if (string.Equals(_playerPath, path, StringComparison.OrdinalIgnoreCase)) return;
        _previewRevision++;
        _pendingPreviewFrame = null;
        PausedFrame.Source = null;
        _mediaReady = false;
        _playerPath = path;
        Player.Close();
        Player.Source = new Uri(path);
        Player.Play();
        if (!_playing) Player.Pause();
    }

    void SyncBasePlayback()
    {
        if (_timeline == null) return;
        if (_position >= _timeline.MainDuration || _timeline.Resolve(_position) is not { } resolved)
        {
            _activeClip = -1;
            Player.Pause();
            Player.Visibility = PausedFrame.Visibility = Visibility.Collapsed;
            return;
        }
        bool changed = _activeClip != resolved.ClipIndex;
        _activeClip = resolved.ClipIndex;
        ActivateBaseSource(_timeline.Clips[_activeClip]);
        Player.Visibility = Visibility.Visible;
        Player.Opacity = PreviewOpacity(_timeline.Clips[_activeClip], _position - _timeline.StartOf(_activeClip));
        if (_mediaReady && (changed || Math.Abs((Player.Position - resolved.SourceTime).TotalSeconds) > .18))
            Player.Position = resolved.SourceTime;
        if (changed) { Player.Play(); UpdatePreviewLayout(); }
    }

    TrackPreview CreateTrackPreview(VideoEditTrack track)
    {
        var player = new MediaElement
        {
            LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Manual, ScrubbingEnabled = true,
            Stretch = Stretch.Fill, IsHitTestVisible = false, Visibility = Visibility.Collapsed
        };
        var still = new Image { Stretch = Stretch.Fill, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        PreviewCanvas.Children.Add(player);
        PreviewCanvas.Children.Add(still);
        var preview = new TrackPreview(player, still);
        player.MediaOpened += (_, _) => { preview.Ready = true; if (!_closed) SyncTrackPlayback(force: true); };
        player.MediaFailed += (_, e) =>
        {
            preview.Ready = false;
            if (!_closed) StatusText.Text = Loc.Format("EditorPlaybackFailed", e.ErrorException.Message);
        };
        return preview;
    }

    void SyncTrackPlayback(bool force = false)
    {
        if (_timeline == null || _closed) return;
        foreach (VideoEditTrack track in _timeline.Tracks)
        {
            if (!_trackPlayers.TryGetValue(track.Id, out TrackPreview? preview))
                _trackPlayers.Add(track.Id, preview = CreateTrackPreview(track));
            TimeSpan at = _position >= _timeline.Duration && _timeline.Duration > TimeSpan.Zero ? _position - TimeSpan.FromTicks(1) : _position;
            VideoEditClip? clip = track.Clips.FirstOrDefault(clip => at >= clip.Position && at < clip.Position + clip.Duration);
            if (clip == null)
            {
                preview.Player.Pause();
                preview.Player.Visibility = preview.Still.Visibility = Visibility.Collapsed;
                preview.ClipId = null;
                preview.Revision++;
                continue;
            }
            bool changed = preview.ClipId != clip.Id;
            preview.ClipId = clip.Id;
            if (preview.Path != clip.SourcePath)
            {
                preview.Player.Close();
                preview.Path = clip.SourcePath;
                preview.Ready = false;
                preview.Still.Source = null;
                preview.Player.Source = new Uri(clip.SourcePath!);
                preview.Player.Play();
                if (!_playing) preview.Player.Pause();
            }
            TimeSpan time = clip.Start + TimeSpan.FromTicks(Math.Clamp((at - clip.Position).Ticks, 0, clip.Duration.Ticks - 1));
            preview.Player.Volume = (track.Muted ? 0 : clip.Volume) * .8;
            preview.Player.Opacity = preview.Still.Opacity = PreviewOpacity(clip, at - clip.Position);
            if (preview.Ready && (changed || force || Math.Abs((preview.Player.Position - time).TotalSeconds) > .18)) preview.Player.Position = time;
            if (_playing)
            {
                preview.FrameCancellation?.Cancel();
                preview.Revision++;
                preview.Still.Visibility = Visibility.Collapsed;
                preview.Player.Visibility = track.Kind == VideoEditTrackKind.Video ? Visibility.Visible : Visibility.Hidden;
                preview.Player.Play();
            }
            else
            {
                preview.Player.Pause();
                preview.Player.Visibility = track.Kind == VideoEditTrackKind.Video ? Visibility.Visible : Visibility.Hidden;
                if ((force || changed) && track.Kind == VideoEditTrackKind.Video) _ = ReadTrackFrameAsync(preview, clip, time);
            }
            if (changed || force) UpdatePreviewLayout();
        }
    }

    async Task ReadTrackFrameAsync(TrackPreview layer, VideoEditClip clip, TimeSpan time)
    {
        long revision = ++layer.Revision;
        layer.FrameCancellation?.Cancel(); layer.FrameCancellation?.Dispose();
        layer.FrameCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        CancellationToken token = layer.FrameCancellation.Token;
        if (!_previews.TryGetValue(clip.SourcePath!, out VideoEditorPreview? preview)) return;
        try
        {
            ImageSource frame = await preview.ReadFrameAsync(new(time, clip.Start, clip.End), token);
            if (_closed || _playing || revision != layer.Revision) return;
            layer.Still.Source = frame;
            layer.Still.Visibility = Visibility.Visible;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Error("Reading additional video track preview.", ex); }
    }

    void PauseTrackPlayback()
    {
        foreach (TrackPreview preview in _trackPlayers.Values) preview.Player.Pause();
        SyncTrackPlayback(force: true);
    }

    void CloseTrackPlayback()
    {
        foreach (TrackPreview preview in _trackPlayers.Values)
        {
            preview.Revision++;
            preview.FrameCancellation?.Cancel(); preview.FrameCancellation?.Dispose();
            preview.Player.Close();
            PreviewCanvas.Children.Remove(preview.Player);
            PreviewCanvas.Children.Remove(preview.Still);
        }
        _trackPlayers.Clear();
    }

    void RefreshTracks()
    {
        TrackHeaders.Children.Clear();
        if (_timeline == null) return;
        foreach (Guid id in _trackPlayers.Keys.Where(id => !_timeline.Tracks.Any(track => track.Id == id)).ToArray())
        {
            TrackPreview preview = _trackPlayers[id];
            preview.Revision++;
            preview.FrameCancellation?.Cancel(); preview.FrameCancellation?.Dispose();
            preview.Player.Close();
            PreviewCanvas.Children.Remove(preview.Player);
            PreviewCanvas.Children.Remove(preview.Still);
            _trackPlayers.Remove(id);
        }
        void Header(Guid? id, string label, double top, bool muted)
        {
            var panel = new StackPanel { Width = 101, Orientation = Orientation.Horizontal, Margin = new Thickness(2, 0, 2, 0) };
            var select = new Button
            {
                Content = new TextBlock { Text = label, TextTrimming = TextTrimming.CharacterEllipsis }, Width = 78,
                FontSize = 10, Padding = new Thickness(3, 4, 3, 4), ToolTip = label,
                Background = id == _timeline.SelectedTrackId ? (Brush)FindResource("CardHoverBrush") : Brushes.Transparent,
                Foreground = (Brush)FindResource("TextBrush"), BorderThickness = new Thickness(0)
            };
            select.Click += (_, _) =>
            {
                Pause();
                _timeline.SelectedTrackId = id;
                TimelineCanvas.ClearRange();
                _timeline.SelectedIndex = _timeline.SelectedClips.Count > 0 ? 0 : -1;
                RefreshSelection(); RefreshTracks();
            };
            panel.Children.Add(select);
            var mute = new Button
            {
                Content = muted ? "×" : "♫", Width = 22, FontSize = 11, Padding = new Thickness(2),
                Background = Brushes.Transparent, Foreground = muted ? Brushes.Salmon : (Brush)FindResource("MutedBrush"),
                BorderThickness = new Thickness(0), ToolTip = Loc.Get("EditorMuteTrack")
            };
            mute.Click += (_, _) => { if (_timeline.ToggleMute(id)) AfterEdit(); };
            panel.Children.Add(mute);
            Canvas.SetTop(panel, TimelineCanvas.LaneY(top));
            TrackHeaders.Children.Add(panel);
        }
        Header(null, Loc.Get("EditorVideoTrack") + " 1", 37, _timeline.MainMuted);
        var audioLabel = new TextBlock { Text = Loc.Get("EditorAudioTrack"), FontSize = 10, Foreground = Brushes.MediumAquamarine, Margin = new Thickness(9, 0, 0, 0) };
        Canvas.SetTop(audioLabel, TimelineCanvas.LaneY(120));
        TrackHeaders.Children.Add(audioLabel);
        for (int i = 0; i < _timeline.Tracks.Count; i++)
        {
            VideoEditTrack track = _timeline.Tracks[i];
            Header(track.Id, track.Name, 182 + i * 64, track.Muted);
        }
        TimelineCanvas.UpdateLaneHeight(); TrackHeaders.Height = TimelineCanvas.Height;
        UpdatePreviewLayout();
    }

    void RefreshInspector()
    {
        _refreshingInspector = true;
        try
        {
            VideoEditClip? clip = _timeline?.SelectedClip;
            SelectedClipLabel.Text = clip == null ? Loc.Get("EditorSelectClip") : Path.GetFileName(clip.SourcePath ?? _sourcePath);
            SelectedClipLabel.ToolTip = clip?.SourcePath ?? _sourcePath;
            VideoFrameTransform transform = clip?.Transform ?? new();
            _workingTransform = null;
            string Percent(double value) => (value * 100).ToString("0.##", CultureInfo.InvariantCulture);
            CropLeftBox.Text = Percent(transform.Left); CropTopBox.Text = Percent(transform.Top);
            CropRightBox.Text = Percent(transform.Right); CropBottomBox.Text = Percent(transform.Bottom);
            ScaleBox.Text = Percent(transform.Scale); OffsetXBox.Text = Percent(transform.OffsetX); OffsetYBox.Text = Percent(transform.OffsetY);
            FrameScaleSlider.Value = transform.Scale * 100;
            FrameScaleLabel.Text = Percent(transform.Scale) + "%";
            RefreshFades(clip);
            VolumeSlider.Value = (clip?.Volume ?? 1) * 100;
            VolumeLabel.Text = $"{VolumeSlider.Value:0}%";
            TrackPositionBox.Text = VideoTimelineControl.Format(_timeline?.SelectedStart ?? TimeSpan.Zero, true);
        }
        finally { _refreshingInspector = false; }
    }

    void Preview_SizeChanged(object sender, SizeChangedEventArgs e) => UpdatePreviewLayout();
    void EditorLayout_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        _editorViewportHeight = e.NewSize.Height; UpdatePanelMinimums();
    }
    void PanelLayout_SizeChanged(object sender, SizeChangedEventArgs e) => UpdatePanelMinimums();
    void UpdatePanelMinimums()
    {
        if (EditorLayout == null || TracksRow == null || TimelineToolbar == null || TrimTools == null || EditorHeader == null || EditorFooter == null) return;
        double OuterHeight(FrameworkElement element) => element.ActualHeight + element.Margin.Top + element.Margin.Bottom;
        TracksRow.MinHeight = Math.Max(160, OuterHeight(TimelineToolbar) + OuterHeight(TrimTools) + 72);
        EditorWorkspace.MinHeight = TracksRow.MinHeight + PreviewRow.MinHeight + 9;
        EditorLayout.MinHeight = Math.Max(720, OuterHeight(EditorHeader) + EditorWorkspace.MinHeight + OuterHeight(EditorFooter));
        EditorLayout.Height = Math.Max(EditorLayout.MinHeight, _editorViewportHeight - 32);
    }

    Matrix FrameMatrix(VideoEditorSource source, VideoFrameTransform transform)
    {
        if (_source == null) return Matrix.Identity;
        double canvasWidth = PreviewCanvas.ActualWidth, canvasHeight = PreviewCanvas.ActualHeight;
        double canvasScale = Math.Max(.001, Math.Min((canvasWidth - 32) / _source.Width, (canvasHeight - 32) / _source.Height));
        double cw = source.Width * (1 - transform.Left - transform.Right), ch = source.Height * (1 - transform.Top - transform.Bottom);
        double rw = transform.QuarterTurns % 2 == 1 ? ch : cw, rh = transform.QuarterTurns % 2 == 1 ? cw : ch;
        double fit = Math.Min(_source.Width / rw, _source.Height / rh) * transform.Scale;
        var matrix = Matrix.Identity;
        matrix.Translate(-source.Width * transform.Left, -source.Height * transform.Top);
        matrix.Rotate(transform.QuarterTurns * 90);
        if (transform.QuarterTurns == 1) matrix.Translate(ch, 0);
        else if (transform.QuarterTurns == 2) matrix.Translate(cw, ch);
        else if (transform.QuarterTurns == 3) matrix.Translate(0, cw);
        matrix.Scale(fit * canvasScale, fit * canvasScale);
        matrix.Translate((canvasWidth - _source.Width * canvasScale) / 2 + ((_source.Width - rw * fit) / 2 + transform.OffsetX * _source.Width) * canvasScale,
            (canvasHeight - _source.Height * canvasScale) / 2 + ((_source.Height - rh * fit) / 2 + transform.OffsetY * _source.Height) * canvasScale);
        return matrix;
    }

    void LayoutFrame(FrameworkElement element, VideoEditClip clip)
    {
        if (!_previews.TryGetValue(clip.SourcePath ?? _sourcePath!, out VideoEditorPreview? preview)) return;
        VideoEditorSource source = preview.Source;
        VideoFrameTransform transform = DisplayTransform(clip);
        element.Width = source.Width;
        element.Height = source.Height;
        element.Clip = new RectangleGeometry(new Rect(source.Width * transform.Left, source.Height * transform.Top,
            source.Width * (1 - transform.Left - transform.Right), source.Height * (1 - transform.Top - transform.Bottom)));
        element.RenderTransform = new MatrixTransform(FrameMatrix(source, transform));
    }

    void UpdatePreviewLayout()
    {
        if (_timeline == null || _source == null || PreviewCanvas.ActualWidth <= 0 || PreviewCanvas.ActualHeight <= 0) return;
        double fit = Math.Max(.001, Math.Min((PreviewCanvas.ActualWidth - 32) / _source.Width, (PreviewCanvas.ActualHeight - 32) / _source.Height));
        PreviewCanvas.Clip = new RectangleGeometry(new Rect((PreviewCanvas.ActualWidth - _source.Width * fit) / 2,
            (PreviewCanvas.ActualHeight - _source.Height * fit) / 2, _source.Width * fit, _source.Height * fit));
        Canvas.SetLeft(OutputCanvasBorder, (PreviewCanvas.ActualWidth - _source.Width * fit) / 2);
        Canvas.SetTop(OutputCanvasBorder, (PreviewCanvas.ActualHeight - _source.Height * fit) / 2);
        OutputCanvasBorder.Width = _source.Width * fit; OutputCanvasBorder.Height = _source.Height * fit;
        if (_timeline.Resolve(_position) is { } resolved && HasBaseFrame)
        {
            VideoEditClip clip = _timeline.Clips[resolved.ClipIndex];
            LayoutFrame(Player, clip); LayoutFrame(PausedFrame, clip);
            Player.Opacity = PausedFrame.Opacity = PreviewOpacity(clip, _position - _timeline.StartOf(resolved.ClipIndex));
            Player.Visibility = Visibility.Visible;
        }
        else Player.Visibility = PausedFrame.Visibility = Visibility.Collapsed;
        foreach (VideoEditTrack track in _timeline.Tracks.Where(track => track.Kind == VideoEditTrackKind.Video))
            if (_trackPlayers.TryGetValue(track.Id, out TrackPreview? preview) && track.Clips.FirstOrDefault(clip => clip.Id == preview.ClipId) is { } clip)
            {
                LayoutFrame(preview.Player, clip); LayoutFrame(preview.Still, clip);
                preview.Player.Opacity = preview.Still.Opacity = PreviewOpacity(clip, _position - clip.Position);
            }
        UpdateFrameAdorner();
    }

    VideoFrameTransform DisplayTransform(VideoEditClip clip)
    {
        if (clip.Id != _timeline?.SelectedClip?.Id) return clip.Transform;
        VideoFrameTransform transform = _workingTransform ?? clip.Transform;
        return _frameMode == "crop" ? UncroppedTransform(transform) : transform;
    }

    static VideoFrameTransform UncroppedTransform(VideoFrameTransform transform) => transform with
    { Left = 0, Top = 0, Right = 0, Bottom = 0, Scale = 1, OffsetX = 0, OffsetY = 0 };

    void UpdateFrameAdorner()
    {
        FrameAdorner.Visibility = Visibility.Collapsed;
        if (_playing || _source == null || _timeline?.SelectedClip is not { } clip || _timeline.SelectedTrack?.Kind == VideoEditTrackKind.Audio ||
            !_previews.TryGetValue(clip.SourcePath ?? _sourcePath!, out VideoEditorPreview? preview)) return;
        bool active = _timeline.SelectedTrackId == null
            ? HasBaseFrame && _timeline.Resolve(_position)?.ClipIndex == _timeline.SelectedIndex
            : _position >= clip.Position && _position <= clip.Position + clip.Duration;
        if (!active) return;
        VideoEditorSource source = preview.Source;
        VideoFrameTransform transform = _workingTransform ?? clip.Transform;
        Matrix matrix = FrameMatrix(source, _frameMode == "crop" ? UncroppedTransform(transform) : transform);
        FrameAdorner.Frame = Rect.Transform(new Rect(source.Width * transform.Left, source.Height * transform.Top,
            source.Width * (1 - transform.Left - transform.Right), source.Height * (1 - transform.Top - transform.Bottom)), matrix);
        FrameAdorner.SourceBounds = Rect.Transform(new Rect(0, 0, source.Width, source.Height), matrix);
        FrameAdorner.Cropping = _frameMode == "crop";
        FrameAdorner.Visibility = Visibility.Visible;
        FrameAdorner.InvalidateVisual();
    }

    async void AddVideo_Click(object sender, RoutedEventArgs e)
    {
        if (_timeline == null || _loading || _exporting || _voiceRecorder != null) return;
        Pause();
        var dialog = new OpenFileDialog { Filter = "Video (*.mp4;*.mov;*.m4v;*.wmv;*.avi)|*.mp4;*.mov;*.m4v;*.wmv;*.avi", Multiselect = true, Title = Loc.Get("EditorAddVideo") };
        if (dialog.ShowDialog(this) != true) return;
        if (_timeline.SelectedTrack?.Kind == VideoEditTrackKind.Audio) _timeline.SelectedTrackId = null;
        _loading = true; RefreshControls();
        try
        {
            foreach (string file in dialog.FileNames)
            {
                await AddVideoSourceAsync(file);
                if (_closed) return;
            }
            AfterEdit(selectStart: true);
        }
        catch (Exception ex) { StatusText.Text = ex.Message; Log.Error("Importing editor video.", ex); }
        finally { _loading = false; if (!_closed) { RefreshTracks(); RefreshControls(); } }
    }

    async Task AddVideoSourceAsync(string file)
    {
        if (_timeline == null) return;
        string path = Path.GetFullPath(file);
        VideoEditorPreview preview = await VideoEditorMedia.OpenPreviewAsync(path);
        if (_closed) return;
        _previews[path] = preview;
        TimeSpan at = _timeline.SelectedTrackId == null ? _timeline.MainDuration : _position;
        if (!_timeline.AddSource(path, preview.Source.Duration, at, VideoEditTrackKind.Video))
            throw new InvalidOperationException(Loc.Get("EditorTrimInvalid"));
        _ = ReadAdditionalWaveformAsync(path, preview.Source.Duration);
    }

    async void AddAudio_Click(object sender, RoutedEventArgs e)
    {
        if (_timeline == null || _loading || _exporting || _voiceRecorder != null) return;
        Pause();
        var dialog = new OpenFileDialog { Filter = "Audio (*.wav;*.mp3;*.flac;*.m4a)|*.wav;*.mp3;*.flac;*.m4a", Multiselect = true, Title = Loc.Get("EditorAddAudio") };
        if (dialog.ShowDialog(this) != true) return;
        _loading = true; RefreshControls();
        try
        {
            foreach (string file in dialog.FileNames)
            {
                TimeSpan duration = await VideoEditorMedia.ReadAudioDurationAsync(file);
                if (_closed) return;
                if (_timeline.SelectedTrack?.Kind != VideoEditTrackKind.Audio) NewTrack(VideoEditTrackKind.Audio);
                _timeline.AddSource(Path.GetFullPath(file), duration, _position, VideoEditTrackKind.Audio);
                _ = ReadAdditionalWaveformAsync(file, duration);
            }
            AfterEdit(selectStart: true);
        }
        catch (Exception ex) { StatusText.Text = ex.Message; Log.Error("Importing editor audio.", ex); }
        finally { _loading = false; if (!_closed) { RefreshTracks(); RefreshControls(); } }
    }

    async Task ReadAdditionalWaveformAsync(string path, TimeSpan duration)
    {
        try
        {
            CancellationToken token = _waveformCancellation?.Token ?? _lifetime.Token;
            AudioWaveform waveform = await Task.Run(() => AudioWaveform.Read(path, duration, token), token);
            if (_closed || token.IsCancellationRequested) return;
            TimelineCanvas.Waveforms[path] = waveform;
            TimelineCanvas.InvalidateVisual();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Error("Reading additional track waveform.", ex); }
    }

    void NewTrack(VideoEditTrackKind kind)
    {
        if (_timeline == null) return;
        int number = _timeline.Tracks.Count(track => track.Kind == kind) + (kind == VideoEditTrackKind.Video ? 2 : 1);
        _timeline.AddTrack(kind, Loc.Get(kind == VideoEditTrackKind.Video ? "EditorVideoTrack" : "EditorAudioTrack") + " " + number);
    }

    void NewVideoTrack_Click(object sender, RoutedEventArgs e) { Pause(); NewTrack(VideoEditTrackKind.Video); AfterEdit(); }
    void NewAudioTrack_Click(object sender, RoutedEventArgs e) { Pause(); NewTrack(VideoEditTrackKind.Audio); AfterEdit(); }
    void RemoveTrack_Click(object sender, RoutedEventArgs e) { if (_timeline?.RemoveSelectedTrack() == true) AfterEdit(); }

    void Rotate(int delta)
    {
        if (_timeline?.SelectedClip is not { } clip) return;
        if (_timeline.SetSelectedTransform(clip.Transform with { QuarterTurns = (clip.Transform.QuarterTurns + delta + 4) % 4 })) AfterEdit();
    }
    void RotateLeft_Click(object sender, RoutedEventArgs e) => Rotate(-1);
    void RotateRight_Click(object sender, RoutedEventArgs e) => Rotate(1);
    void ResetFrame_Click(object sender, RoutedEventArgs e) { if (_timeline?.SetSelectedTransform(new()) == true) AfterEdit(); }

    void ApplyFrame_Click(object sender, RoutedEventArgs e)
    {
        if (_timeline?.SelectedClip is not { } clip) return;
        bool Read(TextBox box, out double value) => double.TryParse(box.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        if (!Read(CropLeftBox, out double left) || !Read(CropTopBox, out double top) || !Read(CropRightBox, out double right) ||
            !Read(CropBottomBox, out double bottom) || !Read(ScaleBox, out double scale) || !Read(OffsetXBox, out double x) || !Read(OffsetYBox, out double y))
        { StatusText.Text = Loc.Get("EditorFrameInvalid"); return; }
        var transform = clip.Transform with { Left = left / 100, Top = top / 100, Right = right / 100, Bottom = bottom / 100, Scale = scale / 100, OffsetX = x / 100, OffsetY = y / 100 };
        if (!transform.IsValid) { StatusText.Text = Loc.Get("EditorFrameInvalid"); return; }
        if (_timeline.SetSelectedTransform(transform)) AfterEdit();
    }

    void ApplyPosition_Click(object sender, RoutedEventArgs e)
    {
        if (_timeline == null) return;
        if (!TimeSpan.TryParseExact(TrackPositionBox.Text, @"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture, out TimeSpan position) ||
            position < TimeSpan.Zero || !_timeline.SetSelectedPosition(position))
        { StatusText.Text = Loc.Get("EditorPositionInvalid"); return; }
        AfterEdit(selectStart: true);
    }

    void Volume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_refreshingInspector || VolumeLabel == null) return;
        VolumeLabel.Text = $"{e.NewValue:0}%";
    }
    void CommitVolume() { if (!_refreshingInspector && _timeline?.SetSelectedVolume(VolumeSlider.Value / 100) == true) AfterEdit(); }

    void FrameScale_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_refreshingInspector || _timeline?.SelectedClip is not { } clip) return;
        Pause();
        _frameMode = "move";
        FinishCropButton.Visibility = Visibility.Collapsed;
        _workingTransform = clip.Transform with { Scale = e.NewValue / 100 };
        FrameScaleLabel.Text = $"{e.NewValue:0}%";
        UpdatePreviewLayout();
    }

    void CommitFrameScale()
    {
        if (_refreshingInspector || _workingTransform == null) return;
        VideoFrameTransform transform = _workingTransform;
        _workingTransform = null;
        if (_timeline?.SetSelectedTransform(transform) == true) AfterEdit();
    }

    void CropSelect_Click(object sender, RoutedEventArgs e) => BeginFrameGesture("crop");
    void MoveFrame_Click(object sender, RoutedEventArgs e) => BeginFrameGesture("move");
    void BeginFrameGesture(string mode)
    {
        if (_timeline?.SelectedClip is not { } clip || _timeline.SelectedTrack?.Kind == VideoEditTrackKind.Audio) return;
        Pause(); Seek(_timeline.SelectedStart);
        _frameMode = mode;
        FinishCropButton.Visibility = mode == "crop" ? Visibility.Visible : Visibility.Collapsed;
        MoveFrameButton.Background = (Brush)FindResource(mode == "move" ? "AccentBrush" : "CardBrush");
        CropSelectButton.Background = (Brush)FindResource(mode == "crop" ? "AccentBrush" : "CardBrush");
        PreviewViewport.Cursor = mode == "crop" ? Cursors.Cross : Cursors.SizeAll;
        StatusText.Text = Loc.Get(mode == "crop" ? "EditorCropDragHint" : "EditorMoveDragHint");
        UpdatePreviewLayout();
    }

    void Preview_Down(object sender, MouseButtonEventArgs e)
    {
        if (_playing || _exporting || _loading || _timeline?.SelectedClip is not { } clip || _timeline.SelectedTrack?.Kind == VideoEditTrackKind.Audio || _source == null ||
            !_previews.TryGetValue(clip.SourcePath ?? _sourcePath!, out VideoEditorPreview? preview)) return;
        Point point = e.GetPosition(PreviewCanvas);
        _frameHandle = FrameAdorner.HitHandle(point);
        if (FrameAdorner.Visibility != Visibility.Visible || (_frameMode != "crop" && _frameHandle < 0 && !FrameAdorner.Frame.Contains(point))) return;
        _framePress = point;
        _gestureTransform = clip.Transform;
        _workingTransform = clip.Transform;
        _gestureMatrix = FrameMatrix(preview.Source, _frameMode == "crop" ? UncroppedTransform(clip.Transform) : clip.Transform);
        _gestureBounds = FrameAdorner.Frame;
        _frameGesture = _frameMode == "crop" ? (_frameHandle >= 0 ? "crop-resize" : Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && FrameAdorner.Frame.Contains(point) ? "crop-move" : "crop-new")
            : _frameHandle >= 0 ? "resize" : "move";
        if (_frameGesture == "crop-resize")
        {
            Matrix inverse = _gestureMatrix; inverse.Invert();
            Point sourcePoint = inverse.Transform(FrameAdorner.Handles[_frameHandle]);
            double left = clip.Transform.Left * preview.Source.Width, right = (1 - clip.Transform.Right) * preview.Source.Width;
            double top = clip.Transform.Top * preview.Source.Height, bottom = (1 - clip.Transform.Bottom) * preview.Source.Height;
            _cropEdgeX = Math.Abs(sourcePoint.X - left) < 1 ? -1 : Math.Abs(sourcePoint.X - right) < 1 ? 1 : 0;
            _cropEdgeY = Math.Abs(sourcePoint.Y - top) < 1 ? -1 : Math.Abs(sourcePoint.Y - bottom) < 1 ? 1 : 0;
        }
        PreviewViewport.CaptureMouse();
        e.Handled = true;
    }

    void Preview_Move(object sender, MouseEventArgs e)
    {
        Point point = e.GetPosition(PreviewCanvas);
        if (_framePress is not { } press || _frameGesture == null || _source == null || _gestureTransform is not { } original || _timeline?.SelectedClip is not { } clip)
        {
            if (!_playing && FrameAdorner.Visibility == Visibility.Visible)
            {
                int handle = FrameAdorner.HitHandle(point);
                PreviewViewport.Cursor = handle < 0 ? (_frameMode == "crop" && !FrameAdorner.Frame.Contains(point) ? Cursors.Cross : Cursors.SizeAll)
                    : handle % 4 == 0 ? Cursors.SizeNWSE : handle % 4 == 2 ? Cursors.SizeNESW : handle % 4 == 1 ? Cursors.SizeNS : Cursors.SizeWE;
            }
            return;
        }
        if (!_previews.TryGetValue(clip.SourcePath ?? _sourcePath!, out VideoEditorPreview? preview)) return;
        double fit = Math.Max(.001, Math.Min((PreviewCanvas.ActualWidth - 32) / _source.Width, (PreviewCanvas.ActualHeight - 32) / _source.Height));
        VideoFrameTransform transform = original;
        if (_frameGesture.StartsWith("crop", StringComparison.Ordinal))
        {
            Matrix inverse = _gestureMatrix; if (!inverse.HasInverse) return; inverse.Invert();
            Point a = inverse.Transform(press), b = inverse.Transform(point);
            double x = Math.Clamp(b.X / preview.Source.Width, 0, 1), y = Math.Clamp(b.Y / preview.Source.Height, 0, 1);
            double left = original.Left, top = original.Top, right = 1 - original.Right, bottom = 1 - original.Bottom;
            if (_frameGesture == "crop-new")
            {
                double ax = Math.Clamp(a.X / preview.Source.Width, 0, 1), ay = Math.Clamp(a.Y / preview.Source.Height, 0, 1);
                left = Math.Min(ax, x); right = Math.Max(ax, x); top = Math.Min(ay, y); bottom = Math.Max(ay, y);
                if (right - left < .05 || bottom - top < .05) return;
            }
            else if (_frameGesture == "crop-move")
            {
                double width = right - left, height = bottom - top;
                left = Math.Clamp(left + (b.X - a.X) / preview.Source.Width, 0, 1 - width);
                top = Math.Clamp(top + (b.Y - a.Y) / preview.Source.Height, 0, 1 - height);
                right = left + width; bottom = top + height;
            }
            else
            {
                if (_cropEdgeX < 0) left = Math.Min(x, right - .05);
                else if (_cropEdgeX > 0) right = Math.Max(x, left + .05);
                if (_cropEdgeY < 0) top = Math.Min(y, bottom - .05);
                else if (_cropEdgeY > 0) bottom = Math.Max(y, top + .05);
            }
            transform = original with { Left = left, Top = top, Right = 1 - right, Bottom = 1 - bottom };
        }
        else if (_frameGesture == "move")
            transform = original with { OffsetX = Math.Clamp(original.OffsetX + (point.X - press.X) / (_source.Width * fit), -1, 1),
                OffsetY = Math.Clamp(original.OffsetY + (point.Y - press.Y) / (_source.Height * fit), -1, 1) };
        else if (_frameGesture == "resize")
        {
            Point[] handles =
            [ _gestureBounds.TopLeft, new(_gestureBounds.Left + _gestureBounds.Width / 2, _gestureBounds.Top), _gestureBounds.TopRight,
                new(_gestureBounds.Right, _gestureBounds.Top + _gestureBounds.Height / 2), _gestureBounds.BottomRight,
                new(_gestureBounds.Left + _gestureBounds.Width / 2, _gestureBounds.Bottom), _gestureBounds.BottomLeft,
                new(_gestureBounds.Left, _gestureBounds.Top + _gestureBounds.Height / 2) ];
            Point anchor = handles[(_frameHandle + 4) % 8];
            Vector before = handles[_frameHandle] - anchor, after = point - anchor;
            double ratio = Math.Clamp(Vector.Multiply(after, before) / Math.Max(.001, before.LengthSquared), .1 / original.Scale, 4 / original.Scale);
            var center = new Point(_gestureBounds.Left + _gestureBounds.Width / 2, _gestureBounds.Top + _gestureBounds.Height / 2);
            Point moved = anchor + (center - anchor) * ratio;
            transform = original with { Scale = original.Scale * ratio,
                OffsetX = Math.Clamp(original.OffsetX + (moved.X - center.X) / (_source.Width * fit), -1, 1),
                OffsetY = Math.Clamp(original.OffsetY + (moved.Y - center.Y) / (_source.Height * fit), -1, 1) };
        }
        if (!transform.IsValid) return;
        _workingTransform = transform;
        FrameScaleLabel.Text = $"{transform.Scale * 100:0}%";
        UpdatePreviewLayout();
        e.Handled = true;
    }

    void Preview_Up(object sender, MouseButtonEventArgs e)
    {
        if (_framePress == null) return;
        VideoFrameTransform? transform = _workingTransform;
        _framePress = null; _frameGesture = null; _gestureTransform = _workingTransform = null;
        PreviewViewport.ReleaseMouseCapture();
        if (transform?.IsValid == true && _timeline?.SetSelectedTransform(transform) == true) AfterEdit();
        UpdatePreviewLayout();
        e.Handled = true;
    }

    void Voice_Click(object sender, RoutedEventArgs e)
    {
        if (_voiceRecorder != null) { Pause(); return; }
        if (_timeline == null || _loading || _exporting || _timeline.Duration <= TimeSpan.Zero) return;
        Pause();
        if (_position >= _timeline.Duration) Seek(TimeSpan.Zero);
        string? path = null;
        try
        {
            string directory = Path.Combine(AppPaths.ResolveOutputDirectory(SettingsStore.Load().OutputDirectory), "Voiceovers");
            path = Path.Combine(directory, $"Voiceover_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}_{Guid.NewGuid().ToString("N")[..6]}.wav");
            Directory.CreateDirectory(directory);
            var recorder = new VoiceoverRecorder(path, (MicrophoneBox.SelectedItem as AudioDeviceInfo)?.Id);
            try { recorder.Start(); }
            catch { recorder.Dispose(); throw; }
            _voiceRecorder = recorder;
            _voiceStart = _position;
            VoiceButton.Content = Loc.Get("EditorStopVoice");
            VoiceButton.Tag = FindResource("EditorIconStop");
            VoiceTimeLabel.Text = "00:00:00.000";
            StatusText.Text = Loc.Get("EditorVoiceRecording");
            TogglePlay();
            RefreshControls();
        }
        catch (Exception ex)
        {
            Log.Error("Starting voiceover.", ex);
            StatusText.Text = Loc.Format("EditorVoiceFailed", ex.Message);
            if (path != null && File.Exists(path) && new FileInfo(path).Length <= 44) File.Delete(path);
        }
    }

    void PumpVoice()
    {
        if (_voiceRecorder == null) return;
        try
        {
            _voiceRecorder.Pump(_position - _voiceStart);
            VoiceLevel.Value = _voiceRecorder.Level;
            VoiceTimeLabel.Text = VideoTimelineControl.Format(_position - _voiceStart, true);
        }
        catch (Exception ex) { Pause(); StatusText.Text = Loc.Format("EditorVoiceFailed", ex.Message); }
    }

    void StopVoice()
    {
        if (_voiceRecorder is not { } recorder) return;
        _voiceRecorder = null;
        TimeSpan duration = TimeSpan.Zero;
        string? failure = null;
        try { duration = recorder.Stop(_position - _voiceStart); }
        catch (Exception ex) { duration = recorder.Duration; failure = Loc.Format("EditorVoiceFailed", ex.Message); StatusText.Text = failure; Log.Error("Stopping voiceover.", ex); }
        finally { recorder.Dispose(); }
        VoiceButton.Content = Loc.Get("EditorRecordVoice");
        VoiceButton.Tag = FindResource("EditorIconVoice");
        VoiceLevel.Value = 0;
        VoiceTimeLabel.Text = "";
        if (duration >= VideoEditTimeline.MinimumDuration && _timeline != null)
        {
            _timeline.AddTrack(VideoEditTrackKind.Audio, Loc.Get("EditorVoiceTrack") + " " + (_timeline.Tracks.Count(track => track.Kind == VideoEditTrackKind.Audio) + 1));
            _timeline.AddSource(recorder.FilePath, duration, _voiceStart, VideoEditTrackKind.Audio);
            _ = ReadAdditionalWaveformAsync(recorder.FilePath, duration);
            StatusText.Text = Loc.Format("EditorVoiceSaved", recorder.FilePath);
            if (failure != null) StatusText.Text += Environment.NewLine + failure;
        }
        RefreshSelection(); RefreshTracks(); UpdateExtent(); RefreshControls();
    }
}
