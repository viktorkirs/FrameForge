namespace FrameForge.Core;

public static class RegionPlacement
{
    // Coordinates are physical pixels; align relative to the monitor for H.264 cropping.
    public static ScreenRect MoveWithin(ScreenRect region, int dx, int dy, ScreenRect monitor)
    {
        int width = Math.Min(region.Width, monitor.Width) & ~1;
        int height = Math.Min(region.Height, monitor.Height) & ~1;
        if (width < 16 || height < 16)
            return default;
        int x = (int)Math.Clamp((long)region.X + dx - monitor.X, 0, monitor.Width - width) & ~1;
        int y = (int)Math.Clamp((long)region.Y + dy - monitor.Y, 0, monitor.Height - height) & ~1;
        return new ScreenRect(monitor.X + x, monitor.Y + y, width, height);
    }
}
