using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FrameForge.Capture;
using FrameForge.Core;
using FrameForge.UI;

internal static partial class Program
{
    static void RunScreenshotTests()
    {
        var red = new Border { Background = Brushes.Red };
        var blue = new Border { Background = Brushes.Blue };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(blue, 1);
        grid.Children.Add(red);
        grid.Children.Add(blue);
        var target = new Window { Title = "FrameForge screenshot test", WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize, Width = 400, Height = 240, Left = 60, Top = 60,
            Topmost = true, Content = grid };
        target.Show();
        Wait(Task.Delay(200));
        try
        {
            nint hwnd = new WindowInteropHelper(target).Handle;
            IReadOnlyList<MonitorInfo> monitors = MonitorEnumerator.GetMonitors();
            MonitorInfo monitor = WindowCatalog.MonitorContaining(monitors, hwnd)!;
            Point redPoint = red.PointToScreen(new Point(30, 30));
            Point bluePoint = blue.PointToScreen(new Point(30, 30));
            var redRegion = new ScreenRect((int)redPoint.X & ~1, (int)redPoint.Y & ~1, 64, 48);
            var blueRegion = new ScreenRect((int)bluePoint.X & ~1, (int)bluePoint.Y & ~1, 64, 48);

            Test("standalone monitor screenshots retain full dimensions", () =>
            {
                BgraFrame frame = FrameGrabber.Capture(monitor);
                Check((frame.Width, frame.Height) == (monitor.Width, monitor.Height), "Monitor screenshot was cropped");
            });
            Test("standalone crops use monitor-relative coordinates before scaling", () =>
            {
                var request = new CaptureRequest { Monitor = monitor, CropX = blueRegion.X - monitor.Left,
                    CropY = blueRegion.Y - monitor.Top, CropWidth = 65, CropHeight = 49 };
                BgraFrame frame = FrameGrabber.Capture(request);
                Check((frame.Width, frame.Height) == (65, 49), "Crop lost pixels or returned the full monitor");
                CheckScreenshotColor(frame.Pixels, frame.Width, frame.Height, blue: true);
                BgraFrame scaled = FrameGrabber.Capture(request, 32);
                Check((scaled.Width, scaled.Height) == (32, 24), "Scaling used monitor dimensions instead of the crop");
                CheckScreenshotColor(scaled.Pixels, scaled.Width, scaled.Height, blue: true);
            });
            Test("screenshot command saves and copies the selected region and follows movement", () =>
            {
                MainViewModel model = ScreenshotModel("region", CaptureMode.Region, blueRegion);
                try
                {
                    CheckScreenshotOutput(model, 64, 48, blue: true);
                    Check((bool)typeof(MainViewModel).GetMethod("TryMoveRegion", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .Invoke(model, [redRegion])!, "Region movement failed");
                    CheckScreenshotOutput(model, 64, 48, blue: false);
                }
                finally { Wait(model.ShutdownAsync()); }
            });
            Test("window screenshot command captures the selected window behind another window", () =>
            {
                var cover = new Window { WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
                    Left = target.Left, Top = target.Top, Width = target.Width, Height = target.Height,
                    Topmost = true, Background = Brushes.Lime };
                MainViewModel model = ScreenshotModel("window", CaptureMode.Window);
                typeof(MainViewModel).GetField("_window", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(model, hwnd);
                cover.Show();
                Wait(Task.Delay(200));
                try
                {
                    WindowCatalog.TryGetBounds(hwnd, out _, out _, out int width, out int height);
                    CheckScreenshotOutput(model, width, height, blue: true, xFraction: .75);
                }
                finally { cover.Close(); Wait(model.ShutdownAsync()); }
            });
            Test("region screenshots retain the selected target during recording and pause", () =>
            {
                MainViewModel model = ScreenshotModel("recording", CaptureMode.Region, blueRegion);
                model.Settings.Encoder = EncoderPreference.Software;
                try
                {
                    model.RequestRecord();
                    Wait((Task)typeof(MainViewModel).GetField("_recordingTask", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(model)!);
                    Check(model.IsRecording, "Screenshot test recording did not start: " + model.Hint);
                    Wait(Task.Delay(200));
                    CheckScreenshotOutput(model, 64, 48, blue: true);
                    model.PauseCommand.Execute(null);
                    Wait(Task.Delay(200));
                    Check(model.IsPaused, "Screenshot test recording did not pause");
                    CheckScreenshotOutput(model, 64, 48, blue: true);
                }
                finally { Wait(model.ShutdownAsync()); }
            });
            Test("missing windows and empty or off-screen regions never save a full-screen fallback", () =>
            {
                foreach ((string name, CaptureMode mode, ScreenRect region, string errorKey) in new[] {
                    ("missing-window", CaptureMode.Window, default(ScreenRect), "WindowHint"),
                    ("empty-region", CaptureMode.Region, default(ScreenRect), "RegionHint"),
                    ("offscreen-region", CaptureMode.Region, new ScreenRect(-100000, -100000, 64, 48), "RegionOffScreen") })
                {
                    MainViewModel model = ScreenshotModel(name, mode, region);
                    try
                    {
                        model.RequestScreenshot();
                        Check(Directory.GetFiles(model.Settings.OutputDirectory!, "Screenshot_*.png").Length == 0,
                            "Invalid target saved a screenshot: " + name);
                        Check(model.Hint == Loc.Get(errorKey) && model.ScreenshotCommand.CanExecute(null), "Invalid target did not report the selection error or allow retry");
                    }
                    finally { Wait(model.ShutdownAsync()); }
                }
            });
            Test("minimized windows report an error and allow a screenshot after restoration", () =>
            {
                MainViewModel model = ScreenshotModel("minimized-window", CaptureMode.Window);
                typeof(MainViewModel).GetField("_window", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(model, hwnd);
                try
                {
                    target.WindowState = WindowState.Minimized;
                    Wait(Task.Delay(100));
                    model.RequestScreenshot();
                    Check(Directory.GetFiles(model.Settings.OutputDirectory!, "Screenshot_*.png").Length == 0 &&
                        model.Hint == Loc.Get("WindowMinimized"), "Minimized window saved a screenshot or hid the error");
                    target.WindowState = WindowState.Normal;
                    Wait(Task.Delay(150));
                    WindowCatalog.TryGetBounds(hwnd, out _, out _, out int width, out int height);
                    CheckScreenshotOutput(model, width, height, blue: true, xFraction: .75);
                }
                finally { target.WindowState = WindowState.Normal; Wait(model.ShutdownAsync()); }
            });
            Test("closed windows report that the selected target disappeared", () =>
            {
                var closed = new Window { Width = 100, Height = 100 };
                closed.Show();
                nint closedHandle = new WindowInteropHelper(closed).Handle;
                closed.Close();
                MainViewModel model = ScreenshotModel("closed-window", CaptureMode.Window);
                typeof(MainViewModel).GetField("_window", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(model, closedHandle);
                typeof(MainViewModel).GetField("_windowTitle", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(model, "Closed test window");
                try
                {
                    model.RequestScreenshot();
                    Check(Directory.GetFiles(model.Settings.OutputDirectory!, "Screenshot_*.png").Length == 0 &&
                        model.Hint == Loc.Get("WindowGone"), "Closed window saved a screenshot or hid the error");
                }
                finally { Wait(model.ShutdownAsync()); }
            });
        }
        finally { target.Close(); }
    }

    static MainViewModel ScreenshotModel(string name, CaptureMode mode, ScreenRect region = default)
    {
        string directory = Path.Combine(_output, name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        SettingsStore.Save(new AppSettings { Language = AppLanguage.English, Mode = mode,
            RegionX = region.X, RegionY = region.Y, RegionWidth = region.Width, RegionHeight = region.Height,
            SystemAudio = false, Microphone = false, CaptureCursor = false, SmoothCursor = false, OutputDirectory = directory });
        return new MainViewModel();
    }

    static void CheckScreenshotOutput(MainViewModel model, int width, int height, bool blue, double xFraction = .5)
    {
        string directory = model.Settings.OutputDirectory!;
        string[] before = Directory.GetFiles(directory, "Screenshot_*.png");
        model.RequestScreenshot();
        string[] added = Directory.GetFiles(directory, "Screenshot_*.png").Except(before).ToArray();
        Check(added.Length == 1, "Screenshot command did not save exactly one PNG: " + model.Hint);
        using var file = File.OpenRead(added[0]);
        BitmapSource image = BitmapFrame.Create(file, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        Check((image.PixelWidth, image.PixelHeight) == (width, height), "Saved PNG has the wrong target dimensions");
        var pixels = new byte[width * height * 4];
        new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0).CopyPixels(pixels, width * 4, 0);
        CheckScreenshotColor(pixels, width, height, blue, xFraction);
        BitmapSource clipboard = Clipboard.GetImage() ?? throw new Exception("Screenshot missing from clipboard");
        Check((clipboard.PixelWidth, clipboard.PixelHeight) == (width, height), "Clipboard image has the wrong target dimensions");
        new FormatConvertedBitmap(clipboard, PixelFormats.Bgra32, null, 0).CopyPixels(pixels, width * 4, 0);
        CheckScreenshotColor(pixels, width, height, blue, xFraction);
    }

    static void CheckScreenshotColor(byte[] pixels, int width, int height, bool blue, double xFraction = .5)
    {
        int index = ((height / 2 * width) + (int)(width * xFraction)) * 4;
        int b = pixels[index], g = pixels[index + 1], r = pixels[index + 2];
        Check(blue ? b > r + 100 && b > g + 100 : r > b + 100 && r > g + 100,
            $"Screenshot contains the wrong area or an overlapping window: {r}/{g}/{b}");
    }
}
