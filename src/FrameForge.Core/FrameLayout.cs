namespace FrameForge.Core;

public static class FrameLayout
{
    public static ScreenRect Fit(int sourceWidth, int sourceHeight, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sourceWidth, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(sourceHeight, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        double scale = Math.Min(width / (double)sourceWidth, height / (double)sourceHeight);
        int fittedWidth = Math.Clamp((int)Math.Round(sourceWidth * scale), 1, width);
        int fittedHeight = Math.Clamp((int)Math.Round(sourceHeight * scale), 1, height);
        return new ScreenRect((width - fittedWidth) / 2, (height - fittedHeight) / 2, fittedWidth, fittedHeight);
    }
}
