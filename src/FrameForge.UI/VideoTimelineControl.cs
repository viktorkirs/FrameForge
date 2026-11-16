using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FrameForge.Core;
using FrameForge.Encoding;

namespace FrameForge.UI;

/// <summary>Absolute-time editing on every lane, with range selection and adjustable lane density.</summary>
public sealed class VideoTimelineControl : FrameworkElement
{
    const double Inset = 12;
    static readonly Brush BackgroundBrush = Brush("#131925"), VideoBrush = Brush("#274C80"), AudioBrush = Brush("#194F49");
    static readonly Brush SelectedBrush = Brush("#8AB7FF"), TextBrush = Brush("#E5ECF8"), MutedBrush = Brush("#8697AF");
    static readonly Brush WaveBrush = Brush("#67D9BD"), HeadBrush = Brush("#FFCD78");
    static readonly Brush HoverVideoBrush = Brush("#355F96"), HoverAudioBrush = Brush("#24685D"), HoverBorderBrush = Brush("#C4DEFF");
    VideoEditTimeline? _timeline;
    TimeSpan _position;
    Point _press;
    int _pressedIndex = -1, _edge;
    int _hoverIndex = -1, _hoverEdge;
    Guid? _pressedTrack, _hoverTrack;
    TimeSpan _dragPosition;
    bool _hoverPlayhead;
    bool _dragging, _scrubbing, _selectingRange, _drawingLanes;
    double _fitSeconds = 1, _trackHeightScale = 1;
    TimeSpan _rangeAnchor;
    TimeSpan _trimStart, _trimEnd;

    public VideoTimelineControl()
    {
        Height = 182;
        Focusable = true;
        ClipToBounds = true;
        IsEnabledChanged += (_, _) => { if (!IsEnabled) ClearHover(); };
    }

    public VideoEditTimeline? Timeline { get => _timeline; set { _timeline = value; FitToContent(); ClearRange(); UpdateLaneHeight(); InvalidateVisual(); } }
    public AudioWaveform? Waveform { get; set; }
    public IReadOnlyDictionary<long, ImageSource> Thumbnails { get; set; } = new Dictionary<long, ImageSource>();
    public Dictionary<(string Path, long Ticks), ImageSource> SourceThumbnails { get; } = new();
    public Dictionary<string, AudioWaveform> Waveforms { get; } = new(StringComparer.OrdinalIgnoreCase);
    public double PixelsPerSecond { get; private set; } = 50;
    public TimeSpan Position { get => _position; set { _position = value; InvalidateVisual(); } }
    public double TrackHeightScale { get => _trackHeightScale; set { _trackHeightScale = Math.Clamp(value, .45, 1.75); UpdateLaneHeight(); InvalidateVisual(); } }
    public bool RangeSelectionMode { get; set; }
    public TimeSpan? RangeStart { get; private set; }
    public TimeSpan? RangeEnd { get; private set; }
    public bool HasRange => RangeStart != null && RangeEnd - RangeStart >= VideoEditTimeline.MinimumDuration;
    public double LaneY(double y) => 32 + (y - 32) * TrackHeightScale;
    public void UpdateLaneHeight() => Height = LaneY(182 + (Timeline?.Tracks.Count ?? 0) * 64);
    public void FitToContent() => _fitSeconds = Math.Max(1, Timeline?.Duration.TotalSeconds ?? 1) * 1.12 + .5;
    public void ClearRange() { RangeStart = RangeEnd = null; RangeChanged?.Invoke(); InvalidateVisual(); }
    public void SetRange(TimeSpan start, TimeSpan end)
    {
        RangeStart = start < end ? start : end; RangeEnd = start < end ? end : start;
        RangeChanged?.Invoke(); InvalidateVisual();
    }
    public event Action<TimeSpan>? SeekRequested;
    public event Action<TimeSpan>? Edited;
    public event Action? SelectionChanged;
    public event Action? InteractionStarted;
    public event Action? InteractionCompleted;
    public event Action? RangeChanged;
    public event Action? EditRejected;
    public event Action<TimeSpan, TimeSpan, TimeSpan>? TrimPreviewRequested;

    public void UpdateExtent(double viewportWidth, double zoom)
    {
        double seconds = Math.Max(_fitSeconds, (Timeline?.Duration.TotalSeconds ?? 0) * 1.12 + .5);
        PixelsPerSecond = Math.Max(.001, (Math.Max(300, viewportWidth) - Inset * 2) / _fitSeconds * zoom);
        Width = Math.Max(viewportWidth, seconds * PixelsPerSecond + Inset * 2);
        UpdateLaneHeight();
        if (IsMouseOver && !IsMouseCaptured) UpdateHover(Mouse.GetPosition(this));
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        dc.DrawRectangle(BackgroundBrush, null, new Rect(RenderSize));
        ScrollViewer? scroll = FindScrollViewer();
        double visibleLeft = scroll?.HorizontalOffset ?? 0;
        double visibleRight = visibleLeft + (scroll?.ViewportWidth ?? ActualWidth);
        double[] steps = [.1, .2, .5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 1800, 3600];
        double step = steps.FirstOrDefault(value => value * PixelsPerSecond >= 85, 7200);
        for (double seconds = Math.Max(0, Math.Floor((visibleLeft - Inset) / PixelsPerSecond / step) * step);
            X(TimeSpan.FromSeconds(seconds)) <= visibleRight; seconds += step)
        {
            double x = X(TimeSpan.FromSeconds(seconds));
            dc.DrawLine(new Pen(Brushes.DimGray, .5), new Point(x, 26), new Point(x, Height - 11));
            Text(dc, Format(TimeSpan.FromSeconds(seconds), milliseconds: step < 1), x + 5, 5, MutedBrush, 10);
        }
        dc.PushTransform(new ScaleTransform(1, TrackHeightScale, 0, 32));
        _drawingLanes = true;
        if (Timeline != null)
        {
            for (int index = 0; index < Timeline.Clips.Count; index++)
            {
                VideoEditClip clip = Timeline.Clips[index];
                if (_pressedTrack == null && _pressedIndex == index)
                {
                    if (_edge != 0) clip = clip with { Start = _trimStart, End = _trimEnd, Position = clip.Position + _trimStart - clip.Start };
                    else if (_dragging) clip = clip with { Position = _dragPosition };
                }
                double x = X(clip.Position);
                double width = Math.Max(1, clip.Duration.TotalSeconds * PixelsPerSecond);
                if (x + width >= visibleLeft && x <= visibleRight)
                    DrawClip(dc, clip, index, x, width, visibleLeft, visibleRight);
            }
            for (int lane = 0; lane < Timeline.Tracks.Count; lane++)
            {
                VideoEditTrack track = Timeline.Tracks[lane];
                double y = 182 + lane * 64;
                dc.DrawLine(new Pen(Brush("#343C4E"), .6), new Point(0, y), new Point(ActualWidth, y));
                if (track.Clips.Count == 0) Text(dc, Loc.Get("EditorEmptyTrack"), visibleLeft + 16, y + 23, MutedBrush, 11);
                for (int index = 0; index < track.Clips.Count; index++)
                {
                    VideoEditClip clip = track.Clips[index];
                    if (_pressedTrack == track.Id && _pressedIndex == index)
                    {
                        if (_edge != 0) clip = clip with { Start = _trimStart, End = _trimEnd, Position = clip.Position + _trimStart - clip.Start };
                        else if (_dragging) clip = clip with { Position = _dragPosition };
                    }
                    double x = X(clip.Position), width = Math.Max(1, clip.Duration.TotalSeconds * PixelsPerSecond);
                    if (x + width >= visibleLeft && x <= visibleRight) DrawTrackClip(dc, clip, track, index, x, y, width, visibleLeft, visibleRight);
                }
            }
        }
        _drawingLanes = false;
        dc.Pop();
        if (RangeStart is { } start && RangeEnd is { } end && end > start)
        {
            double left = X(start), right = X(end);
            dc.DrawRectangle(Brush("#354FAAFF"), null, new Rect(left, 0, right - left, Height));
            dc.DrawLine(new Pen(SelectedBrush, 2), new Point(left, 0), new Point(left, Height));
            dc.DrawLine(new Pen(SelectedBrush, 2), new Point(right, 0), new Point(right, Height));
        }
        double head = X(Position);
        dc.DrawLine(new Pen(HeadBrush, 1.5), new Point(head, 24), new Point(head, Height - 10));
        double headRadius = _hoverPlayhead || _scrubbing ? 6 : 4;
        dc.DrawEllipse(HeadBrush, null, new Point(head, 25), headRadius, headRadius);
    }

    void DrawClip(DrawingContext dc, VideoEditClip clip, int index, double x, double width, double left, double right)
    {
        bool selected = Timeline!.SelectedTrackId == null && index == Timeline.SelectedIndex;
        bool hovered = _hoverTrack == null && index == _hoverIndex || _pressedTrack == null && index == _pressedIndex && IsMouseCaptured && !_scrubbing;
        var video = new Rect(x + 1, 37, Math.Max(.5, width - 2), 59);
        var audio = new Rect(x + 1, 101, Math.Max(.5, width - 2), 57);
        Pen? border = hovered ? new Pen(HoverBorderBrush, 2) : selected ? new Pen(SelectedBrush, 1.5) : new Pen(Brush("#365573"), .7);
        dc.DrawRoundedRectangle(hovered ? HoverVideoBrush : VideoBrush, border, video, 4, 4);
        dc.DrawRoundedRectangle(hovered ? HoverAudioBrush : AudioBrush, border, audio, 4, 4);
        DrawFades(dc, clip, video);
        dc.PushClip(new RectangleGeometry(new Rect(x + 3, 39, Math.Max(0, width - 6), 55)));
        double labelX = Math.Max(x + 13, left + 8);
        ImageSource? thumbnail = null;
        if (clip.SourcePath != null) SourceThumbnails.TryGetValue((clip.SourcePath, clip.Start.Ticks), out thumbnail);
        if (thumbnail == null) Thumbnails.TryGetValue(clip.Start.Ticks, out thumbnail);
        if (thumbnail != null && width > 100 && TrackHeightScale >= .7)
        {
            dc.DrawImage(thumbnail, new Rect(labelX, 45, 60, 35));
            labelX += 69;
        }
        Text(dc, $"{index + 1:00}   {Format(clip.Duration)}  {System.IO.Path.GetFileName(clip.SourcePath)}", labelX, 45, TextBrush, 12);
        if (TrackHeightScale >= .8) Text(dc, $"{Format(clip.Start)} → {Format(clip.End)}", labelX, 66, MutedBrush, 10);
        dc.Pop();
        dc.PushClip(new RectangleGeometry(audio));
        AudioWaveform? sourceWaveform = clip.SourcePath != null && Waveforms.TryGetValue(clip.SourcePath, out AudioWaveform? found) ? found : Waveform;
        if (sourceWaveform is { HasAudio: true, Peaks.Length: > 0 } waveform)
        {
            double first = Math.Max(x + 8, left), last = Math.Min(x + width - 8, right);
            var pen = new Pen(WaveBrush, 1.2);
            for (double pixel = first; pixel < last; pixel += 2)
            {
                double sourceSeconds = clip.Start.TotalSeconds + (pixel - x) / PixelsPerSecond;
                int bucket = (int)Math.Clamp(sourceSeconds / Timeline.SourceLength(clip).TotalSeconds * waveform.Peaks.Length, 0, waveform.Peaks.Length - 1);
                double half = Math.Max(.6, Math.Sqrt(waveform.Peaks[bucket]) * 21);
                dc.DrawLine(pen, new Point(pixel, 130 - half), new Point(pixel, 130 + half));
            }
        }
        else
        {
            dc.DrawLine(new Pen(MutedBrush, .5), new Point(x + 8, 130), new Point(x + width - 8, 130));
        }
        dc.Pop();
        if ((selected || hovered) && width >= 24)
            foreach (int side in new[] { -1, 1 })
            {
                bool active = _pressedTrack == null && index == _pressedIndex && _edge == side || _hoverTrack == null && index == _hoverIndex && _hoverEdge == side;
                double handleWidth = active ? 12 : hovered ? 7 : 4;
                double top = active ? 44 : hovered ? 52 : 61;
                double height = active ? 108 : hovered ? 90 : 72;
                double handleX = side < 0 ? x + 2 : x + width - handleWidth - 2;
                dc.DrawRoundedRectangle(active ? Brushes.White : SelectedBrush, null, new Rect(handleX, top, handleWidth, height), 2, 2);
                if (active)
                    dc.DrawLine(new Pen(VideoBrush, 1), new Point(handleX + handleWidth / 2, 87), new Point(handleX + handleWidth / 2, 108));
            }
    }

    void DrawTrackClip(DrawingContext dc, VideoEditClip clip, VideoEditTrack track, int index, double x, double y, double width, double left, double right)
    {
        bool selected = Timeline!.SelectedTrackId == track.Id && Timeline.SelectedIndex == index;
        bool hovered = _hoverTrack == track.Id && _hoverIndex == index;
        var rect = new Rect(x + 1, y + 4, Math.Max(.5, width - 2), 55);
        Brush fill = track.Kind == VideoEditTrackKind.Video ? (hovered ? HoverVideoBrush : VideoBrush) : (hovered ? HoverAudioBrush : AudioBrush);
        dc.PushOpacity(track.Muted ? .45 : 1);
        dc.DrawRoundedRectangle(fill, new Pen(hovered ? HoverBorderBrush : selected ? SelectedBrush : MutedBrush, selected || hovered ? 1.5 : .5), rect, 4, 4);
        if (track.Kind == VideoEditTrackKind.Video) DrawFades(dc, clip, rect);
        dc.PushClip(new RectangleGeometry(rect));
        double labelX = Math.Max(x + 10, left + 8);
        if (track.Kind == VideoEditTrackKind.Video && clip.SourcePath != null && SourceThumbnails.TryGetValue((clip.SourcePath, clip.Start.Ticks), out ImageSource? image) && width > 120)
        { dc.DrawImage(image, new Rect(labelX, y + 10, 48, 28)); labelX += 55; }
        Text(dc, System.IO.Path.GetFileName(clip.SourcePath) + $"  {Format(clip.Duration)}", labelX, y + 9, TextBrush, 11);
        double middle = y + 41;
        if (clip.SourcePath != null && Waveforms.TryGetValue(clip.SourcePath, out AudioWaveform? waveform) && waveform.Peaks.Length > 0)
        {
            var pen = new Pen(WaveBrush, 1);
            for (double pixel = Math.Max(x + 7, left); pixel < Math.Min(x + width - 7, right); pixel += 2)
            {
                double time = clip.Start.TotalSeconds + (pixel - x) / PixelsPerSecond;
                int bucket = (int)Math.Clamp(time / Timeline.SourceLength(clip).TotalSeconds * waveform.Peaks.Length, 0, waveform.Peaks.Length - 1);
                double half = Math.Max(.5, Math.Sqrt(waveform.Peaks[bucket]) * 12);
                dc.DrawLine(pen, new Point(pixel, middle - half), new Point(pixel, middle + half));
            }
        }
        else dc.DrawLine(new Pen(MutedBrush, .5), new Point(x + 7, middle), new Point(x + width - 7, middle));
        dc.Pop();
        if ((selected || hovered) && width >= 24)
            foreach (int side in new[] { -1, 1 })
            {
                bool active = hovered && _hoverEdge == side || _pressedTrack == track.Id && _pressedIndex == index && _edge == side;
                double handle = active ? 9 : 4;
                dc.DrawRoundedRectangle(active ? Brushes.White : SelectedBrush, null, new Rect(side < 0 ? x + 2 : x + width - handle - 2, y + 12, handle, 39), 2, 2);
            }
        dc.Pop();
    }

    void DrawFades(DrawingContext dc, VideoEditClip clip, Rect rect)
    {
        var pen = new Pen(SelectedBrush, 1);
        if (clip.FadeIn > TimeSpan.Zero) dc.DrawLine(pen, rect.BottomLeft, new Point(rect.Left + Math.Min(rect.Width, clip.FadeIn.TotalSeconds * PixelsPerSecond), rect.Top));
        if (clip.FadeOut > TimeSpan.Zero) dc.DrawLine(pen, new Point(rect.Right - Math.Min(rect.Width, clip.FadeOut.TotalSeconds * PixelsPerSecond), rect.Top), rect.BottomRight);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!IsEnabled || Timeline == null) return;
        Focus();
        InteractionStarted?.Invoke();
        _press = e.GetPosition(this);
        (_pressedIndex, _edge, _pressedTrack) = HitTarget(_press);
        _dragging = false;
        _selectingRange = RangeSelectionMode || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) || _press.Y < 24;
        if (_selectingRange)
        {
            _rangeAnchor = TimeAt(_press.X); SetRange(_rangeAnchor, _rangeAnchor);
            _pressedIndex = -1; _edge = 0;
            CaptureMouse(); e.Handled = true; return;
        }
        ClearRange();
        _scrubbing = _pressedIndex < 0;
        if (!_scrubbing)
        {
            Timeline.SelectedIndex = _pressedIndex;
            Timeline.SelectedTrackId = _pressedTrack;
            VideoEditClip clip = Timeline.SelectedClips[_pressedIndex];
            _trimStart = clip.Start;
            _trimEnd = clip.End;
            _dragPosition = clip.Position;
            SelectionChanged?.Invoke();
        }
        if (_edge == 0) SeekRequested?.Invoke(TimeAt(_press.X));
        Cursor = _edge != 0 || _scrubbing ? Cursors.SizeWE : Cursors.SizeAll;
        CaptureMouse();
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Point point = e.GetPosition(this);
        if (!IsMouseCaptured || Timeline == null)
        {
            UpdateHover(point);
            return;
        }
        if (_selectingRange) SetRange(_rangeAnchor, TimeAt(point.X));
        else if (_scrubbing) SeekRequested?.Invoke(TimeAt(point.X));
        else if (_edge != 0)
        {
            VideoEditClip original = Timeline.SelectedClips[_pressedIndex];
            TimeSpan delta = TimeSpan.FromSeconds((point.X - _press.X) / PixelsPerSecond);
            var bounds = Timeline.TrimBounds(_pressedIndex);
            if (_edge < 0) _trimStart = TimeSpan.FromTicks(Math.Clamp((original.Start + delta).Ticks, bounds.MinimumStart.Ticks, original.End.Ticks - VideoEditTimeline.MinimumDuration.Ticks));
            else _trimEnd = TimeSpan.FromTicks(Math.Clamp((original.End + delta).Ticks, original.Start.Ticks + VideoEditTimeline.MinimumDuration.Ticks, bounds.MaximumEnd.Ticks));
            TrimPreviewRequested?.Invoke(_edge < 0 ? _trimStart : _trimEnd - TimeSpan.FromTicks(1), _trimStart, _trimEnd);
            Cursor = Cursors.SizeWE;
        }
        else if (Math.Abs(point.X - _press.X) >= SystemParameters.MinimumHorizontalDragDistance)
        {
            _dragging = true;
            _dragPosition = TimeSpan.FromSeconds(Math.Max(0, Timeline.SelectedClips[_pressedIndex].Position.TotalSeconds + (point.X - _press.X) / PixelsPerSecond));
            ScrollViewer? scroll = FindScrollViewer();
            if (scroll != null)
            {
                if (point.X - scroll.HorizontalOffset < 24) scroll.ScrollToHorizontalOffset(scroll.HorizontalOffset - 20);
                else if (point.X - scroll.HorizontalOffset > scroll.ViewportWidth - 24) scroll.ScrollToHorizontalOffset(scroll.HorizontalOffset + 20);
            }
            Cursor = Cursors.SizeAll;
        }
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!IsMouseCaptured || Timeline == null) return;
        bool changed = false;
        int trimmedEdge = _edge;
        if (_selectingRange)
        {
            if (!HasRange) { ClearRange(); SeekRequested?.Invoke(TimeAt(e.GetPosition(this).X)); }
        }
        else if (_edge != 0) changed = Timeline.Trim(_pressedIndex, _trimStart, _trimEnd);
        else if (_dragging)
        {
            bool valid = Timeline.CanPlaceSelected(_dragPosition);
            changed = Timeline.SetSelectedPosition(_dragPosition);
            if (!valid) EditRejected?.Invoke();
        }
        TimeSpan previewPosition = changed ? Timeline.SelectedStart : Position;
        if (changed && trimmedEdge > 0) previewPosition += Timeline.SelectedClip!.Duration - TimeSpan.FromTicks(1);
        ReleaseMouseCapture();
        if (changed) Edited?.Invoke(previewPosition);
        UpdateHover(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _edge = 0;
        _dragging = _scrubbing = _selectingRange = false;
        _pressedIndex = -1;
        _pressedTrack = null;
        if (IsMouseOver) UpdateHover(e.GetPosition(this));
        else ClearHover();
        InteractionCompleted?.Invoke();
        InvalidateVisual();
    }

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        if (!IsMouseCaptured) UpdateHover(e.GetPosition(this));
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (!IsMouseCaptured) ClearHover();
    }

    // Hover and press use the same hit regions, including the enlarged trim handles.
    (int Index, int Edge, Guid? TrackId) HitTarget(Point point)
    {
        if (Timeline == null) return (-1, 0, null);
        point = new Point(point.X, 32 + (point.Y - 32) / TrackHeightScale);
        Guid? trackId = null;
        int index;
        VideoEditClip clip;
        double start;
        if (point.Y >= 182)
        {
            int lane = (int)((point.Y - 182) / 64);
            if (lane >= Timeline.Tracks.Count) return (-1, 0, null);
            VideoEditTrack track = Timeline.Tracks[lane];
            TimeSpan time = TimeAt(point.X);
            index = track.Clips.ToList().FindIndex(item => time >= item.Position && time < item.Position + item.Duration);
            if (index < 0) return (-1, 0, track.Id);
            clip = track.Clips[index]; start = X(clip.Position); trackId = track.Id;
        }
        else
        {
            if (point.Y < 37 || point.Y > 158) return (-1, 0, null);
            index = Hit(point.X);
            if (index < 0) return (-1, 0, null);
            clip = Timeline.Clips[index]; start = X(Timeline.StartOf(index));
        }
        double width = clip.Duration.TotalSeconds * PixelsPerSecond;
        if (width < 24) return (index, 0, trackId);
        double edgeWidth = Math.Min(14, width / 3);
        return (index, point.X - start < edgeWidth ? -1 : start + width - point.X <= edgeWidth ? 1 : 0, trackId);
    }

    void UpdateHover(Point point)
    {
        if (!IsEnabled) { ClearHover(); return; }
        (int index, int edge, Guid? trackId) = HitTarget(point);
        bool head = point.Y <= 36 && Math.Abs(point.X - X(Position)) <= 8;
        bool ruler = point.Y < 37;
        Cursor = edge != 0 || head ? Cursors.SizeWE : index >= 0 ? Cursors.SizeAll : ruler ? Cursors.IBeam : Cursors.Arrow;
        ToolTip = Loc.Get(edge < 0 ? "EditorTrimStart" : edge > 0 ? "EditorTrimEnd" : index >= 0 ? "EditorMoveClip" : "EditorSeek");
        if (_hoverIndex == index && _hoverEdge == edge && _hoverPlayhead == head && _hoverTrack == trackId) return;
        _hoverTrack = trackId;
        _hoverIndex = index;
        _hoverEdge = edge;
        _hoverPlayhead = head;
        InvalidateVisual();
    }

    void ClearHover()
    {
        _hoverIndex = -1;
        _hoverTrack = null;
        _hoverEdge = 0;
        _hoverPlayhead = false;
        Cursor = Cursors.Arrow;
        ToolTip = null;
        InvalidateVisual();
    }

    int Hit(double x)
    {
        if (Timeline == null || x < Inset || x >= X(Timeline.MainDuration)) return -1;
        return Timeline.Resolve(TimeAt(x))?.ClipIndex ?? -1;
    }

    double X(TimeSpan time) => Inset + time.TotalSeconds * PixelsPerSecond;
    TimeSpan TimeAt(double x) => TimeSpan.FromSeconds(Math.Clamp((x - Inset) / PixelsPerSecond, 0, Timeline?.Duration.TotalSeconds ?? 0));
    ScrollViewer? FindScrollViewer()
    {
        for (DependencyObject? parent = VisualTreeHelper.GetParent(this); parent != null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is ScrollViewer scroll) return scroll;
        return null;
    }

    void Text(DrawingContext dc, string text, double x, double y, Brush brush, double size)
    {
        if (_drawingLanes) dc.PushTransform(new ScaleTransform(1, 1 / TrackHeightScale, x, y));
        dc.DrawText(new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip), new Point(x, y));
        if (_drawingLanes) dc.Pop();
    }

    internal static string Format(TimeSpan time, bool milliseconds = false) =>
        $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}" + (milliseconds ? $".{time.Milliseconds:000}" : "");

    static Brush Brush(string color)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(color)!;
        brush.Freeze();
        return brush;
    }
}
