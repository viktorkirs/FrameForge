using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FrameForge.Core;
using FrameForge.Encoding;

namespace FrameForge.UI;

/// <summary>One linked montage, with two visual lanes sharing the same clip boundaries.</summary>
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
    int _pressedIndex = -1, _edge, _insertion = -1;
    int _hoverIndex = -1, _hoverEdge;
    bool _hoverPlayhead;
    bool _dragging, _scrubbing;
    TimeSpan _trimStart, _trimEnd;

    public VideoTimelineControl()
    {
        Height = 182;
        Focusable = true;
        ClipToBounds = true;
        IsEnabledChanged += (_, _) => { if (!IsEnabled) ClearHover(); };
    }

    public VideoEditTimeline? Timeline { get => _timeline; set { _timeline = value; InvalidateVisual(); } }
    public AudioWaveform? Waveform { get; set; }
    public IReadOnlyDictionary<long, ImageSource> Thumbnails { get; set; } = new Dictionary<long, ImageSource>();
    public double PixelsPerSecond { get; private set; } = 50;
    public TimeSpan Position { get => _position; set { _position = value; InvalidateVisual(); } }
    public event Action<TimeSpan>? SeekRequested;
    public event Action<TimeSpan>? Edited;
    public event Action? SelectionChanged;
    public event Action? InteractionStarted;
    public event Action? InteractionCompleted;
    public event Action<TimeSpan, TimeSpan, TimeSpan>? TrimPreviewRequested;

    public void UpdateExtent(double viewportWidth, double zoom)
    {
        double seconds = Math.Max(1, Timeline?.Duration.TotalSeconds ?? 1);
        PixelsPerSecond = Math.Max(.001, (Math.Max(300, viewportWidth) - Inset * 2) / seconds * zoom);
        Width = Math.Max(viewportWidth, seconds * PixelsPerSecond + Inset * 2);
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
            dc.DrawLine(new Pen(Brushes.DimGray, .5), new Point(x, 26), new Point(x, 171));
            Text(dc, Format(TimeSpan.FromSeconds(seconds), milliseconds: step < 1), x + 5, 5, MutedBrush, 10);
        }
        double offset = 0;
        if (Timeline != null)
        {
            for (int index = 0; index < Timeline.Clips.Count; index++)
            {
                VideoEditClip clip = Timeline.Clips[index];
                if (_edge != 0 && _pressedIndex == index) clip = clip with { Start = _trimStart, End = _trimEnd };
                double x = Inset + offset * PixelsPerSecond;
                double width = Math.Max(1, clip.Duration.TotalSeconds * PixelsPerSecond);
                if (x + width >= visibleLeft && x <= visibleRight)
                    DrawClip(dc, clip, index, x, width, visibleLeft, visibleRight);
                offset += clip.Duration.TotalSeconds;
            }
        }
        if (_dragging && _insertion >= 0 && Timeline != null)
        {
            double x = X(Timeline.StartOf(_insertion));
            dc.DrawLine(new Pen(HeadBrush, 4), new Point(x, 30), new Point(x, 163));
        }
        double head = X(Position);
        dc.DrawLine(new Pen(HeadBrush, 1.5), new Point(head, 24), new Point(head, 172));
        double headRadius = _hoverPlayhead || _scrubbing ? 6 : 4;
        dc.DrawEllipse(HeadBrush, null, new Point(head, 25), headRadius, headRadius);
    }

    void DrawClip(DrawingContext dc, VideoEditClip clip, int index, double x, double width, double left, double right)
    {
        bool selected = index == Timeline!.SelectedIndex;
        bool hovered = index == _hoverIndex || index == _pressedIndex && IsMouseCaptured && !_scrubbing;
        var video = new Rect(x + 1, 37, Math.Max(.5, width - 2), 59);
        var audio = new Rect(x + 1, 101, Math.Max(.5, width - 2), 57);
        Pen? border = hovered ? new Pen(HoverBorderBrush, 2) : selected ? new Pen(SelectedBrush, 1.5) : new Pen(Brush("#365573"), .7);
        dc.DrawRoundedRectangle(hovered ? HoverVideoBrush : VideoBrush, border, video, 4, 4);
        dc.DrawRoundedRectangle(hovered ? HoverAudioBrush : AudioBrush, border, audio, 4, 4);
        dc.PushClip(new RectangleGeometry(new Rect(x + 3, 39, Math.Max(0, width - 6), 55)));
        double labelX = Math.Max(x + 13, left + 8);
        if (Thumbnails.TryGetValue(clip.Start.Ticks, out ImageSource? thumbnail) && width > 100)
        {
            dc.DrawImage(thumbnail, new Rect(labelX, 45, 60, 35));
            labelX += 69;
        }
        Text(dc, $"{index + 1:00}   {Format(clip.Duration)}", labelX, 45, TextBrush, 12);
        Text(dc, $"{Format(clip.Start)} → {Format(clip.End)}", labelX, 66, MutedBrush, 10);
        dc.Pop();
        dc.PushClip(new RectangleGeometry(audio));
        if (Waveform is { HasAudio: true, Peaks.Length: > 0 } waveform)
        {
            double first = Math.Max(x + 8, left), last = Math.Min(x + width - 8, right);
            var pen = new Pen(WaveBrush, 1.2);
            for (double pixel = first; pixel < last; pixel += 2)
            {
                double sourceSeconds = clip.Start.TotalSeconds + (pixel - x) / PixelsPerSecond;
                int bucket = (int)Math.Clamp(sourceSeconds / Timeline.SourceDuration.TotalSeconds * waveform.Peaks.Length, 0, waveform.Peaks.Length - 1);
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
                bool active = index == _pressedIndex && _edge == side || index == _hoverIndex && _hoverEdge == side;
                double handleWidth = active ? 12 : hovered ? 7 : 4;
                double top = active ? 44 : hovered ? 52 : 61;
                double height = active ? 108 : hovered ? 90 : 72;
                double handleX = side < 0 ? x + 2 : x + width - handleWidth - 2;
                dc.DrawRoundedRectangle(active ? Brushes.White : SelectedBrush, null, new Rect(handleX, top, handleWidth, height), 2, 2);
                if (active)
                    dc.DrawLine(new Pen(VideoBrush, 1), new Point(handleX + handleWidth / 2, 87), new Point(handleX + handleWidth / 2, 108));
            }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!IsEnabled || Timeline == null) return;
        Focus();
        InteractionStarted?.Invoke();
        _press = e.GetPosition(this);
        (_pressedIndex, _edge) = HitTarget(_press);
        _dragging = false;
        _insertion = -1;
        _scrubbing = _pressedIndex < 0;
        if (!_scrubbing)
        {
            Timeline.SelectedIndex = _pressedIndex;
            VideoEditClip clip = Timeline.Clips[_pressedIndex];
            _trimStart = clip.Start;
            _trimEnd = clip.End;
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
        if (_scrubbing) SeekRequested?.Invoke(TimeAt(point.X));
        else if (_edge != 0)
        {
            VideoEditClip original = Timeline.Clips[_pressedIndex];
            TimeSpan delta = TimeSpan.FromSeconds((point.X - _press.X) / PixelsPerSecond);
            if (_edge < 0) _trimStart = TimeSpan.FromTicks(Math.Clamp((original.Start + delta).Ticks, 0, original.End.Ticks - VideoEditTimeline.MinimumDuration.Ticks));
            else _trimEnd = TimeSpan.FromTicks(Math.Clamp((original.End + delta).Ticks, original.Start.Ticks + VideoEditTimeline.MinimumDuration.Ticks, Timeline.SourceDuration.Ticks));
            TrimPreviewRequested?.Invoke(_edge < 0 ? _trimStart : _trimEnd - TimeSpan.FromTicks(1), _trimStart, _trimEnd);
            Cursor = Cursors.SizeWE;
        }
        else if (Math.Abs(point.X - _press.X) >= SystemParameters.MinimumHorizontalDragDistance)
        {
            _dragging = true;
            _insertion = Timeline.Clips.Count;
            for (int i = 0; i < Timeline.Clips.Count; i++)
            {
                if (point.X < X(Timeline.StartOf(i) + TimeSpan.FromTicks(Timeline.Clips[i].Duration.Ticks / 2)))
                { _insertion = i; break; }
            }
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
        if (_edge != 0) changed = Timeline.Trim(_pressedIndex, _trimStart, _trimEnd);
        else if (_dragging && _insertion >= 0)
            changed = Timeline.Move(_pressedIndex, _insertion > _pressedIndex ? _insertion - 1 : _insertion);
        TimeSpan previewPosition = changed ? Timeline.StartOf(Timeline.SelectedIndex) : Position;
        if (changed && trimmedEdge > 0) previewPosition += Timeline.Clips[Timeline.SelectedIndex].Duration - TimeSpan.FromTicks(1);
        ReleaseMouseCapture();
        if (changed) Edited?.Invoke(previewPosition);
        UpdateHover(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _edge = 0;
        _dragging = _scrubbing = false;
        _pressedIndex = _insertion = -1;
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
    (int Index, int Edge) HitTarget(Point point)
    {
        if (point.Y < 37 || point.Y > 158) return (-1, 0);
        int index = Hit(point.X);
        if (index < 0 || Timeline == null) return (-1, 0);
        double start = X(Timeline.StartOf(index)), width = Timeline.Clips[index].Duration.TotalSeconds * PixelsPerSecond;
        if (width < 24) return (index, 0);
        double edgeWidth = Math.Min(14, width / 3);
        return (index, point.X - start < edgeWidth ? -1 : start + width - point.X <= edgeWidth ? 1 : 0);
    }

    void UpdateHover(Point point)
    {
        if (!IsEnabled) { ClearHover(); return; }
        (int index, int edge) = HitTarget(point);
        bool head = point.Y <= 36 && Math.Abs(point.X - X(Position)) <= 8;
        bool ruler = point.Y < 37;
        Cursor = edge != 0 || head ? Cursors.SizeWE : index >= 0 ? Cursors.SizeAll : ruler ? Cursors.IBeam : Cursors.Arrow;
        ToolTip = Loc.Get(edge < 0 ? "EditorTrimStart" : edge > 0 ? "EditorTrimEnd" : index >= 0 ? "EditorMoveClip" : "EditorSeek");
        if (_hoverIndex == index && _hoverEdge == edge && _hoverPlayhead == head) return;
        _hoverIndex = index;
        _hoverEdge = edge;
        _hoverPlayhead = head;
        InvalidateVisual();
    }

    void ClearHover()
    {
        _hoverIndex = -1;
        _hoverEdge = 0;
        _hoverPlayhead = false;
        Cursor = Cursors.Arrow;
        ToolTip = null;
        InvalidateVisual();
    }

    int Hit(double x)
    {
        if (Timeline == null || x < Inset || x >= X(Timeline.Duration)) return -1;
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

    void Text(DrawingContext dc, string text, double x, double y, Brush brush, double size) =>
        dc.DrawText(new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip), new Point(x, y));

    internal static string Format(TimeSpan time, bool milliseconds = false) =>
        $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}" + (milliseconds ? $".{time.Milliseconds:000}" : "");

    static Brush Brush(string color)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(color)!;
        brush.Freeze();
        return brush;
    }
}
