using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FrameForge.Core;

namespace FrameForge.UI;

/// <summary>The same transparent pixels and geometry are used for preview and export.</summary>
public static class VideoEditorVisuals
{
    public static BitmapSource ReadImage(string path)
    {
        using var stream = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        BitmapSource frame = decoder.Frames[0];
        if (frame.PixelWidth > 16384 || frame.PixelHeight > 16384 || (long)frame.PixelWidth * frame.PixelHeight > 64_000_000)
            throw new InvalidDataException(Loc.Get("EditorImageTooLarge"));
        // Normalize DPI and premultiplication without flattening transparent pixels onto a background.
        var converted = new FormatConvertedBitmap(frame, PixelFormats.Pbgra32, null, 0);
        int stride = frame.PixelWidth * 4;
        byte[] pixels = new byte[stride * frame.PixelHeight]; converted.CopyPixels(pixels, stride, 0);
        var bitmap = BitmapSource.Create(frame.PixelWidth, frame.PixelHeight, 96, 96, PixelFormats.Pbgra32, null, pixels, stride);
        bitmap.Freeze(); return bitmap;
    }

    public static BitmapSource RenderText(VideoEditText text)
    {
        if (!text.IsValid) throw new InvalidDataException(Loc.Get("EditorTextInvalid"));
        var label = new TextBlock
        {
            Text = text.Content, FontFamily = new FontFamily(text.FontFamily), FontSize = text.FontSize,
            FontWeight = text.Bold ? FontWeights.Bold : FontWeights.Normal,
            FontStyle = text.Italic ? FontStyles.Italic : FontStyles.Normal,
            TextDecorations = text.Underline ? System.Windows.TextDecorations.Underline : null,
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(text.Color)),
            TextAlignment = text.Alignment switch { VideoTextAlignment.Left => TextAlignment.Left, VideoTextAlignment.Right => TextAlignment.Right, _ => TextAlignment.Center }, TextWrapping = TextWrapping.Wrap,
            Width = Math.Max(2, text.ReferenceWidth * .8), Padding = new Thickness(8)
        };
        TextOptions.SetTextRenderingMode(label, TextRenderingMode.Grayscale);
        TextOptions.SetTextFormattingMode(label, TextFormattingMode.Ideal);
        label.Measure(new Size(label.Width, double.PositiveInfinity));
        int width = (int)Math.Ceiling(label.Width), height = Math.Max(2, (int)Math.Ceiling(label.DesiredSize.Height));
        if (height > 8192 || (long)width * height > 32_000_000) throw new InvalidDataException(Loc.Get("EditorTextTooLarge"));
        label.Arrange(new Rect(0, 0, width, height)); label.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(label); bitmap.Freeze(); return bitmap;
    }

    public static BitmapSource Transform(BitmapSource bitmap, VideoFrameTransform transform)
    {
        int x = (int)Math.Round(bitmap.PixelWidth * transform.Left), y = (int)Math.Round(bitmap.PixelHeight * transform.Top);
        int width = Math.Max(1, bitmap.PixelWidth - x - (int)Math.Round(bitmap.PixelWidth * transform.Right));
        int height = Math.Max(1, bitmap.PixelHeight - y - (int)Math.Round(bitmap.PixelHeight * transform.Bottom));
        BitmapSource result = new CroppedBitmap(bitmap, new Int32Rect(x, y, width, height));
        if (transform.QuarterTurns != 0) result = new TransformedBitmap(result, new RotateTransform(transform.QuarterTurns * 90));
        result.Freeze(); return result;
    }

    internal static void SaveOverlayImage(BitmapSource bitmap, string path)
    {
        // MediaClip image overlays composite RGB as premultiplied, even when their PNG decoder
        // supplies straight-alpha RGB. Keep the premultiplied channel bytes in this private
        // intermediate; normal PNG encoding of Pbgra32 would unpremultiply and brighten edges.
        var premultiplied = new FormatConvertedBitmap(bitmap, PixelFormats.Pbgra32, null, 0);
        int stride = bitmap.PixelWidth * 4;
        byte[] pixels = new byte[stride * bitmap.PixelHeight]; premultiplied.CopyPixels(pixels, stride, 0);
        var nativePixels = BitmapSource.Create(bitmap.PixelWidth, bitmap.PixelHeight, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(nativePixels));
        using var output = File.Create(path); encoder.Save(output);
    }

    public static double Fit(VideoEditClip clip, double canvasWidth, double canvasHeight, double width, double height) =>
        clip.Kind == VideoEditClipKind.Text ? canvasWidth / clip.Text!.ReferenceWidth : Math.Min(canvasWidth / width, canvasHeight / height);

    public static string Label(VideoEditClip clip) => clip.Kind == VideoEditClipKind.Text
        ? Loc.Get("EditorTextLayer") + ": " + (clip.Text?.Content.ReplaceLineEndings(" ") ?? "")
        : Path.GetFileName(clip.SourcePath) ?? "";
}
