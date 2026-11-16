using System.Windows;
using System.Windows.Media;

namespace FrameForge.UI;

/// <summary>Mouse-sized handles, crop shading and a thirds grid, all in preview coordinates.</summary>
public sealed class VideoFrameAdorner : FrameworkElement
{
    public Rect Frame { get; set; }
    public Rect SourceBounds { get; set; }
    public bool Cropping { get; set; }
    public Point[] Handles => Frame.IsEmpty ? [] :
    [
        Frame.TopLeft, new(Frame.Left + Frame.Width / 2, Frame.Top), Frame.TopRight,
        new(Frame.Right, Frame.Top + Frame.Height / 2), Frame.BottomRight,
        new(Frame.Left + Frame.Width / 2, Frame.Bottom), Frame.BottomLeft, new(Frame.Left, Frame.Top + Frame.Height / 2)
    ];

    public int HitHandle(Point point)
    {
        Point[] handles = Handles;
        for (int i = 0; i < handles.Length; i++)
            if (Math.Abs(point.X - handles[i].X) <= 10 && Math.Abs(point.Y - handles[i].Y) <= 10) return i;
        return -1;
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (Frame.IsEmpty || Frame.Width <= 0 || Frame.Height <= 0) return;
        Brush accent = Cropping ? Brushes.Gold : Brushes.LightSkyBlue;
        if (Cropping && !SourceBounds.IsEmpty)
        {
            dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(165, 0, 0, 0)), null,
                new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(SourceBounds), new RectangleGeometry(Frame)));
            var grid = new Pen(new SolidColorBrush(Color.FromArgb(130, 255, 255, 255)), .7);
            for (int i = 1; i < 3; i++)
            {
                double x = Frame.Left + Frame.Width * i / 3, y = Frame.Top + Frame.Height * i / 3;
                dc.DrawLine(grid, new Point(x, Frame.Top), new Point(x, Frame.Bottom));
                dc.DrawLine(grid, new Point(Frame.Left, y), new Point(Frame.Right, y));
            }
        }
        dc.DrawRectangle(null, new Pen(Brushes.Black, 3), Frame);
        dc.DrawRectangle(null, new Pen(accent, 1.5), Frame);
        foreach (Point point in Handles)
            dc.DrawRoundedRectangle(Brushes.White, new Pen(accent, 1.5), new Rect(point.X - 4.5, point.Y - 4.5, 9, 9), 1.5, 1.5);
    }
}
