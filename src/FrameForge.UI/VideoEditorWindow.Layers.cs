using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using FrameForge.Core;

namespace FrameForge.UI;

public partial class VideoEditorWindow
{
    bool _reorderingTrack;
    Guid? _reorderTrackId;
    double _reorderY, _reorderOriginY;
    int _reorderTarget;
    DispatcherTimer? _trackReorderScroll;
    Line? _trackInsertionLine;

    void ApplyLayerOrder(Guid? id, UIElement player, UIElement still)
    {
        if (_timeline == null) return;
        int index = _timeline.LayerOrder.ToList().IndexOf(id ?? Guid.Empty);
        int z = (_timeline.LayerOrder.Count - index) * 2;
        Panel.SetZIndex(player, z); Panel.SetZIndex(still, z + 1);
    }

    void ApplyBaseLayerOrder() => ApplyLayerOrder(null, Player, PausedFrame);

    Thumb CreateTrackGrip(Guid? id)
    {
        var grip = new Thumb { Width = 12, Height = 26, Style = (Style)FindResource("TrackGrip"), Tag = id ?? Guid.Empty, ToolTip = Loc.Get("EditorReorderTrack") };
        grip.DragStarted += (_, _) => StartTrackReorder(id);
        grip.DragDelta += (_, e) => UpdateTrackReorder(e.VerticalChange);
        grip.DragCompleted += (_, e) => FinishTrackReorder(e.Canceled);
        grip.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && grip.IsDragging) { grip.CancelDrag(); e.Handled = true; } };
        return grip;
    }

    void StartTrackReorder(Guid? id)
    {
        if (_timeline == null || _loading || _exporting || _voiceRecorder != null) return;
        Pause(); _reorderTrackId = id; _reorderingTrack = true;
        if (_timeline.SelectedTrackId != id) { _timeline.SelectedTrackId = id; _timeline.SelectedIndex = _timeline.SelectedClips.Count > 0 ? 0 : -1; }
        RefreshSelection();
        _reorderTarget = _timeline.LayerOrder.ToList().IndexOf(id ?? Guid.Empty);
        _reorderY = _reorderOriginY = TimelineCanvas.LaneY(TimelineCanvas.TrackTop(id) + (id == null ? 75 : 32));
        _trackReorderScroll ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(45) };
        _trackReorderScroll.Tick -= AutoScrollTrackReorder; _trackReorderScroll.Tick += AutoScrollTrackReorder;
        _trackReorderScroll.Start(); UpdateTrackInsertion();
    }

    void UpdateTrackReorder(double verticalChange)
    {
        if (!_reorderingTrack) return;
        // The grip stays in its row until drop; Thumb reports displacement from that fixed origin.
        _reorderY = _reorderOriginY + verticalChange; UpdateTrackInsertion();
    }

    void UpdateTrackInsertion()
    {
        if (_timeline == null || !_reorderingTrack) return;
        _reorderTarget = TimelineCanvas.TrackOrderAt(_reorderY);
        Guid target = _timeline.LayerOrder[_reorderTarget];
        double y = TimelineCanvas.TrackTop(target == Guid.Empty ? null : target);
        int original = _timeline.LayerOrder.ToList().IndexOf(_reorderTrackId ?? Guid.Empty);
        if (_reorderTarget > original) y += target == Guid.Empty ? 150 : 64;
        double actual = TimelineCanvas.LaneY(y);
        TimelineCanvas.TrackInsertionY = actual; TimelineCanvas.InvalidateVisual();
        if (_trackInsertionLine == null)
        {
            _trackInsertionLine = new Line { X1 = 0, X2 = 105, Stroke = (Brush)FindResource("AccentBrush"), StrokeThickness = 3, IsHitTestVisible = false };
            Panel.SetZIndex(_trackInsertionLine, 1000); TrackHeaders.Children.Add(_trackInsertionLine);
        }
        _trackInsertionLine.Y1 = _trackInsertionLine.Y2 = actual;
    }

    void AutoScrollTrackReorder(object? sender, EventArgs e)
    {
        if (!_reorderingTrack || Mouse.LeftButton != MouseButtonState.Pressed) { FinishTrackReorder(canceled: true); return; }
        double y = Mouse.GetPosition(TimelineVertical).Y;
        double delta = y < 24 ? -18 : y > TimelineVertical.ActualHeight - 24 ? 18 : 0;
        if (delta == 0) return;
        double before = TimelineVertical.VerticalOffset;
        TimelineVertical.ScrollToVerticalOffset(before + delta); TimelineVertical.UpdateLayout();
        _reorderY += TimelineVertical.VerticalOffset - before; UpdateTrackInsertion();
    }

    void FinishTrackReorder(bool canceled)
    {
        if (!_reorderingTrack) return;
        _reorderingTrack = false; _trackReorderScroll?.Stop();
        TimelineCanvas.TrackInsertionY = null; TimelineCanvas.InvalidateVisual();
        if (_trackInsertionLine != null) TrackHeaders.Children.Remove(_trackInsertionLine);
        _trackInsertionLine = null;
        if (!canceled && _timeline?.MoveTrack(_reorderTrackId, _reorderTarget) == true)
        { AfterEdit(); ShowSelectedTrack(); }
    }
}
