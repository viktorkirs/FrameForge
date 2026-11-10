using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using FrameForge.Capture;
using FrameForge.Core;
using FrameForge.Encoding;
using Vortice.Direct3D11;

internal static partial class Program
{
    static void RunAuditCoreTests()
    {
        Test("aspect fitting preserves the complete frame inside a fixed video canvas", () =>
        {
            Check(FrameLayout.Fit(1920, 1080, 1280, 720) == new ScreenRect(0, 0, 1280, 720), "Equal aspect size changed");
            Check(FrameLayout.Fit(1000, 1000, 1920, 1080) == new ScreenRect(420, 0, 1080, 1080), "Square window clipped or stretched");
            Check(FrameLayout.Fit(2000, 500, 800, 600) == new ScreenRect(0, 200, 800, 200), "Wide window clipped or stretched");
        });
        Test("block audio timing matches the sample reference across pause boundaries and late packets", AudioTimingReference);
        Test("two-hour audio timeline with 7200 pauses stays sample-aligned", LongAudioTimeline);
    }

    static void AudioTimingReference()
    {
        var random = new Random(81273);
        for (int trial = 0; trial < 120; trial++)
        {
            long epoch = 12_345_678_900;
            var timeline = new AudioTimeline(1);
            timeline.Begin(epoch);
            var pauses = new List<(long Start, long End)>();
            long wall = epoch + random.Next(100, 2000);
            for (int p = 0; p < 20; p++)
            {
                long start = wall + random.Next(1, 4000), end = start + random.Next(0, 10000);
                timeline.Pause(start); timeline.Resume(end); pauses.Add((start, end)); wall = end;
            }
            var expected = new float[12000];
            for (int packet = 0; packet < 12; packet++)
            {
                long stamp = epoch + random.Next(-5000, 160000);
                var pcm = new float[random.Next(10, 200) * 2];
                for (int i = 0; i < pcm.Length; i++) pcm[i] = (float)(random.NextDouble() * 2 - 1);
                timeline.AddPacket(0, pcm, stamp);
                for (int i = 0; i < pcm.Length / 2; i++)
                {
                    long at = stamp + i * 10_000_000L / 48000, removed = 0;
                    if (at < epoch) continue;
                    bool paused = false;
                    foreach (var pause in pauses)
                        if (at >= pause.End) removed += pause.End - pause.Start;
                        else if (at >= pause.Start) { paused = true; break; }
                        else break;
                    if (paused) continue;
                    long index = (long)Math.Round((at - epoch - removed) * (48000 / 10_000_000d));
                    expected[index * 2] = pcm[i * 2]; expected[index * 2 + 1] = pcm[i * 2 + 1];
                }
            }
            var actual = new short[expected.Length];
            timeline.ReadPcm(actual);
            for (int i = 0; i < actual.Length; i++)
                Check(actual[i] == (short)Math.Round(Math.Clamp(expected[i], -1, 1) * short.MaxValue), $"Audio changed at boundary, trial={trial}, sample={i}");
        }
    }

    static void LongAudioTimeline()
    {
        var timeline = new AudioTimeline(2, 1);
        const long epoch = 987_654_321_000;
        timeline.Begin(epoch);
        var first = Enumerable.Repeat(.125f, 1920).ToArray();
        var second = Enumerable.Repeat(.25f, 1920).ToArray();
        var result = new short[1920];
        var watch = Stopwatch.StartNew();
        for (int secondIndex = 0; secondIndex < 7200; secondIndex++)
        {
            long wall = epoch + secondIndex * 10_000_000L;
            timeline.AddPacket(0, first, wall);
            timeline.AddPacket(1, second, wall);
            timeline.ReadPcm(result);
            Check(result.All(value => value == 12288), $"Audio drift/gap at virtual second {secondIndex}");
            timeline.Pause(wall + 200_000);
            timeline.Resume(wall + 10_000_000);
            timeline.AddPacket(0, first, wall + 5_000_000); // Late packet entirely inside the completed pause.
        }
        Console.WriteLine($"  virtual wall=2h, media=144s, pauses=7200, run={watch.Elapsed.TotalSeconds:F2}s");
    }

    static void ReusableReadback(MonitorInfo monitor)
    {
        using GraphicsDevice device = GraphicsDevice.CreateForMonitor(new MonitorHandle(monitor.Handle));
        using var texture = device.Device.CreateTexture2D(new Texture2DDescription
        {
            Width = 1920, Height = 1080, MipLevels = 1, ArraySize = 1, Format = Vortice.DXGI.Format.B8G8R8A8_UNorm,
            SampleDescription = new Vortice.DXGI.SampleDescription(1, 0), BindFlags = BindFlags.RenderTarget, Usage = ResourceUsage.Default
        });
        using var view = device.Device.CreateRenderTargetView(texture);
        device.Context.ClearRenderTargetView(view, new Vortice.Mathematics.Color4(0, 0, 1, 1));
        using var reader = new FrameReadback(device, 1280, 720);
        BgraFrame first = reader.Read(texture, new ScreenRect(0, 0, 1920, 1080), true);
        for (int i = 0; i < 5; i++) reader.Read(texture, new ScreenRect(0, 0, 1920, 1080), true);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 60; i++) reader.Read(texture, new ScreenRect(0, 0, 1920, 1080), true);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Check(allocated < 1024 * 1024, $"Readback still allocates pixel-sized buffers: {allocated} bytes / 60 frames");
        BgraFrame square = reader.Read(texture, new ScreenRect(0, 0, 1000, 1000), true);
        Check(ReferenceEquals(first.Pixels, square.Pixels) && square.Pixels[0] == 0 && square.Pixels[3] == 255, "Resized readback retains old edge pixels");
        Check(square.Pixels[((360 * 1280) + 640) * 4] > 240, "Resized content missing from fitted center");
        reader.Read(texture, new ScreenRect(0, 0, 1920, 1080), true);
        Check(first.Pixels[0] > 240, "Old letterbox survives restoring original aspect");
        Console.WriteLine($"  readback: {allocated} managed bytes / 60 scaled 1080p frames");
    }

    static void ScalerResizeLeases(MonitorInfo monitor)
    {
        using GraphicsDevice device = GraphicsDevice.CreateForMonitor(new MonitorHandle(monitor.Handle));
        Type type = typeof(RecordingEngine).Assembly.GetType("FrameForge.Capture.GpuScaler")!;
        using var scaler = (IDisposable)Activator.CreateInstance(type, [device, 640, 480, 320, 240, BindFlags.None, 0, 0, 640, 480, true])!;
        var convert = type.GetMethod("TryConvert")!;
        var slots = new List<ID3D11Texture2D>();
        var releases = new List<Action>();
        try
        {
            for (int i = 0; i < 8; i++)
            {
                object?[] output = [null, null];
                Check((bool)convert.Invoke(scaler, output)!, "Encoder surface pool exhausted early");
                slots.Add((ID3D11Texture2D)output[0]!); releases.Add((Action)output[1]!);
            }
            for (int i = 0; i < 40; i++)
            {
                type.GetMethod("ResizeInput")!.Invoke(scaler, [i % 2 == 0 ? 1001 : 640, i % 2 == 0 ? 751 : 480]);
                type.GetMethod("UpdateCrop")!.Invoke(scaler, [0, 0, 320, 240]);
                object?[] busy = [null, null];
                Check(!(bool)convert.Invoke(scaler, busy)!, "Resize replaced encoder surfaces that were still leased");
            }
            for (int i = 0; i < releases.Count; i++) releases[i]();
            releases.Clear();
            object?[] next = [null, null];
            Check((bool)convert.Invoke(scaler, next)! && ReferenceEquals(slots[0], next[0]), "Resize changed the fixed encoder output texture identity");
            ((Action)next[1]!).Invoke();
        }
        finally { foreach (Action release in releases) release(); }
    }

    static void ResizingCapture(MonitorInfo monitor, EncoderPreference encoder)
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition()); grid.RowDefinitions.Add(new RowDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition());
        Brush[] colors = [Brushes.Red, Brushes.Lime, Brushes.Blue, Brushes.Yellow];
        for (int i = 0; i < 4; i++)
        {
            var cell = new Border { Background = colors[i] };
            Grid.SetColumn(cell, i % 2); Grid.SetRow(cell, i / 2); grid.Children.Add(cell);
        }
        var target = new Window { Title = "FrameForge resize test", WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            Width = 400, Height = 300, Left = 40, Top = 40, Topmost = true, Content = grid };
        target.Show(); Wait(Task.Delay(100));
        using var engine = new RecordingEngine();
        string path = Path.Combine(_output, $"resize-{encoder}.mp4");
        try
        {
            nint hwnd = new WindowInteropHelper(target).Handle;
            Wait(Task.Run(() => engine.Start(new CaptureRequest { Monitor = monitor, Window = hwnd }, path,
                new AppSettings { Fps = 30, Encoder = encoder, SystemAudio = false, Microphone = false, CaptureCursor = false, TemporaryMarker = true })));
            Wait(Task.Delay(400));
            target.Width = 700; target.Height = 240;
            Wait(Task.Delay(700));
            BgraFrame grown = engine.CopyCurrentFrame()!;
            DpiScale dpi = VisualTreeHelper.GetDpi(target);
            Check(grown.Width == ((int)(700 * dpi.DpiScaleX) & ~1) && grown.Height == ((int)(240 * dpi.DpiScaleY) & ~1), "Capture resources did not grow with the window");
            target.Width = 262; target.Height = 382;
            Wait(Task.Delay(800));
            BgraFrame shrunk = engine.CopyCurrentFrame()!;
            Check(shrunk.Width == (int)(262 * dpi.DpiScaleX) && shrunk.Height == (int)(382 * dpi.DpiScaleY), "Capture resources did not preserve the complete, possibly odd-sized window");
            target.Width = 400; target.Height = 300;
            Wait(Task.Delay(650));
            Wait(Task.Run(engine.Stop));
            Check(engine.LastResult is { Error: null }, "Resize recording failed: " + engine.LastResult?.Error);
            CheckWindowCorners(Mp4Probe.Read(path, 20), 700, 240);
            CheckWindowCorners(Mp4Probe.Read(path, 47), 262, 382);
            CheckWindowCorners(Mp4Probe.Read(path, 70), 400, 300);
        }
        finally { Wait(Task.Run(engine.Stop)); target.Close(); }
    }

    static void CheckWindowCorners(Mp4Probe probe, int sourceWidth, int sourceHeight)
    {
        Check(probe.PreviewPixels != null, "Resized MP4 frame did not decode");
        var image = System.Windows.Media.Imaging.BitmapSource.Create(probe.PreviewWidth, probe.PreviewHeight, 96, 96, PixelFormats.Bgra32, null, probe.PreviewPixels!, probe.PreviewWidth * 4);
        var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
        png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
        using (var file = File.Create(Path.Combine(_output, $"resize-{sourceWidth}x{sourceHeight}.png"))) png.Save(file);
        ScreenRect fitted = FrameLayout.Fit(sourceWidth, sourceHeight, probe.PreviewWidth, probe.PreviewHeight);
        for (int quadrant = 0; quadrant < 4; quadrant++)
        {
            int x = fitted.X + fitted.Width * (quadrant % 2 == 0 ? 1 : 3) / 4;
            int y = fitted.Y + fitted.Height * (quadrant / 2 == 0 ? 1 : 3) / 4;
            int index = (y * probe.PreviewWidth + x) * 4;
            byte b = probe.PreviewPixels![index], g = probe.PreviewPixels[index + 1], r = probe.PreviewPixels[index + 2];
            bool matches = quadrant switch { 0 => r > g + 100 && r > b + 100, 1 => g > r + 100 && g > b + 100,
                2 => b > r + 100 && b > g + 100, _ => r > b + 100 && g > b + 100 };
            Check(matches, $"Resized window quadrant {quadrant} missing: {r}/{g}/{b}");
        }
        if (fitted.X > 5 || fitted.Y > 5)
            Check(probe.PreviewPixels![0] < 20 && probe.PreviewPixels[1] < 20 && probe.PreviewPixels[2] < 20, "Old pixels survive outside resized content");
    }
}
