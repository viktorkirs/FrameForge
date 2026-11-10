using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace FrameForge.UI;

// DWM supplies live previews without starting a separate capture session per card.
sealed class WindowThumbnail(Window owner, FrameworkElement preview, ScrollViewer viewport, TextBlock placeholder, nint source) : IDisposable
{
    nint _thumbnail;
    ThumbnailProperties? _last;
    bool _failed;

    public void Update()
    {
        if (_failed || !preview.IsLoaded || preview.ActualWidth < 1 || preview.ActualHeight < 1)
            return;
        if (_thumbnail == 0 && DwmRegisterThumbnail(new WindowInteropHelper(owner).Handle, source, out _thumbnail) < 0)
        {
            _failed = true;
            return;
        }
        if (DwmQueryThumbnailSourceSize(_thumbnail, out NativeSize size) < 0 || size.Width < 1 || size.Height < 1)
        {
            Hide();
            placeholder.Visibility = Visibility.Visible;
            return;
        }
        Rect host = new(preview.TransformToAncestor(owner).Transform(new Point()), preview.RenderSize);
        Rect visible = new(viewport.TransformToAncestor(owner).Transform(new Point()), viewport.RenderSize);
        visible.Intersect(new Rect(0, 0, owner.ActualWidth, owner.ActualHeight));
        double scale = Math.Min(host.Width / size.Width, host.Height / size.Height);
        Rect fitted = new(host.X + (host.Width - size.Width * scale) / 2,
            host.Y + (host.Height - size.Height * scale) / 2, size.Width * scale, size.Height * scale);
        Rect clipped = Rect.Intersect(fitted, visible);
        if (!preview.IsVisible || clipped.IsEmpty || clipped.Width < 1 || clipped.Height < 1)
        {
            Hide();
            return;
        }
        Matrix toPixels = PresentationSource.FromVisual(owner)!.CompositionTarget!.TransformToDevice;
        Point topLeft = toPixels.Transform(clipped.TopLeft);
        Point bottomRight = toPixels.Transform(clipped.BottomRight);
        var properties = new ThumbnailProperties
        {
            Flags = 0x1 | 0x2 | 0x4 | 0x8 | 0x10,
            Destination = new NativeRect((int)Math.Round(topLeft.X), (int)Math.Round(topLeft.Y), (int)Math.Round(bottomRight.X), (int)Math.Round(bottomRight.Y)),
            Source = new NativeRect(
                Math.Clamp((int)Math.Round((clipped.Left - fitted.Left) / scale), 0, size.Width - 1),
                Math.Clamp((int)Math.Round((clipped.Top - fitted.Top) / scale), 0, size.Height - 1),
                Math.Clamp((int)Math.Round((clipped.Right - fitted.Left) / scale), 1, size.Width),
                Math.Clamp((int)Math.Round((clipped.Bottom - fitted.Top) / scale), 1, size.Height)),
            Opacity = 255, Visible = true, SourceClientAreaOnly = false
        };
        if (_last is ThumbnailProperties previous && previous.Equals(properties))
            return;
        if (DwmUpdateThumbnailProperties(_thumbnail, ref properties) >= 0)
        {
            _last = properties;
            placeholder.Visibility = Visibility.Hidden;
        }
        else
        {
            Hide();
            placeholder.Visibility = Visibility.Visible;
        }
    }

    void Hide()
    {
        if (_thumbnail == 0 || _last is { Visible: false })
            return;
        var properties = new ThumbnailProperties { Flags = 0x8, Visible = false };
        DwmUpdateThumbnailProperties(_thumbnail, ref properties);
        _last = properties;
    }

    public void Dispose()
    {
        if (_thumbnail != 0)
            DwmUnregisterThumbnail(_thumbnail);
        _thumbnail = 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct NativeSize { public int Width; public int Height; }

    [StructLayout(LayoutKind.Sequential)]
    struct NativeRect(int left, int top, int right, int bottom)
    {
        public int Left = left;
        public int Top = top;
        public int Right = right;
        public int Bottom = bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ThumbnailProperties
    {
        public uint Flags;
        public NativeRect Destination;
        public NativeRect Source;
        public byte Opacity;
        [MarshalAs(UnmanagedType.Bool)] public bool Visible;
        [MarshalAs(UnmanagedType.Bool)] public bool SourceClientAreaOnly;
    }

    [DllImport("dwmapi.dll")] static extern int DwmRegisterThumbnail(nint destination, nint source, out nint thumbnail);
    [DllImport("dwmapi.dll")] static extern int DwmUnregisterThumbnail(nint thumbnail);
    [DllImport("dwmapi.dll")] static extern int DwmQueryThumbnailSourceSize(nint thumbnail, out NativeSize size);
    [DllImport("dwmapi.dll")] static extern int DwmUpdateThumbnailProperties(nint thumbnail, ref ThumbnailProperties properties);
}
