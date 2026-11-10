using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FrameForge.Capture;
using FrameForge.Core;
using FrameForge.Encoding;
using FrameForge.UI;

internal static partial class Program
{
    static void RunMarkerMeterTests()
    {
        Test("audio peaks stay independent, retain short signals and return to silence", () =>
        {
            var system = new AudioLevelTracker();
            var mic = new AudioLevelTracker();
            system.Report([.5f, float.NaN, float.PositiveInfinity, -.1f]);
            system.Report([.02f]);
            Check(system.ReadLevel() > .85 && mic.ReadLevel() == 0, "Short system peak lost or leaked to mic");
            Check(system.ReadLevel() == 0, "Consumed peak did not return to silence");
            mic.Report([-.05f]);
            Check(mic.ReadLevel() > .5 && system.ReadLevel() == 0, "Microphone level was coupled to system source");
            mic.Report([1f]);
            mic.Reset();
            Check(mic.ReadLevel() == 0, "Pause/reset kept an old peak");
            system.Report([float.NaN, float.NegativeInfinity, .00001f]);
            Check(system.ReadLevel() == 0, "Invalid samples or background noise activate the meter");
        });
        Test("yellow marker separates strokes, caches snapshots and clears on Alt release", () =>
        {
            var state = new MarkerState();
            state.BeginStroke(10, 10, 12);
            Check(state.Snapshot().Segments.Length == 0, "Disabled marker accepted ink");
            state.SetActive(true);
            state.BeginStroke(10, 10, 12);
            state.AddPoint(100, 10);
            state.EndStroke();
            state.AddPoint(200, 10);
            state.BeginStroke(300, 50, 12);
            MarkerSnapshot ink = state.Snapshot();
            Check(ink.Segments.Length == 3 && ink.Segments[2].Start == new MarkerPoint(300, 50), "Separate strokes were joined");
            Check(ReferenceEquals(ink, state.Snapshot()), "Unchanged snapshot allocated again");
            state.SetActive(false);
            Check(state.Snapshot().Segments.Length == 0 && ink.Segments.Length == 3, "Release did not clear ink or mutated a published snapshot");
            state.SetActive(true);
            state.BeginStroke(0, 0, 12);
            for (int i = 1; i < 9000; i++) state.AddPoint(i * 3, 0);
            Check(state.Snapshot().Segments.Length <= 8192, "Long drawings have unbounded memory");
        });
        Test("marker keyboard hook installs and is released when disabled", () =>
        {
            Type type = typeof(MainWindow).Assembly.GetType("FrameForge.UI.MarkerController")!;
            var state = new MarkerState();
            var controller = (IDisposable)Activator.CreateInstance(type, [state])!;
            try
            {
                type.GetMethod("SetEnabled")!.Invoke(controller, [true]);
                Check((nint)type.GetField("_hook", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(controller)! != 0, "Global Alt hook missing");
                state.SetActive(true);
                state.BeginStroke(10, 10, 12);
                type.GetMethod("SetEnabled")!.Invoke(controller, [false]);
                Check((nint)type.GetField("_hook", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(controller)! == 0 && state.Snapshot().Segments.Length == 0,
                    "Disabling left hook or ink alive");
            }
            finally { controller.Dispose(); }
        });
        Test("screen marker renders translucent yellow ink and removes it", RenderScreenMarker);
        Test("repeated and rapid Alt presses start with fresh empty marker surfaces", RepeatMarkerActivation);
        Test("round meters animate independently and settle when inactive", RenderAudioMeters);
        Test("audio indicator activity follows source toggles, recording and pause", () =>
        {
            var model = new MainViewModel();
            try
            {
                model.SystemAudio = true;
                model.Microphone = false;
                Check(!model.SystemAudioActive && !model.MicrophoneActive, "Idle sources claim to record");
                typeof(MainViewModel).GetProperty(nameof(MainViewModel.IsRecording))!.SetValue(model, true);
                Check(model.SystemAudioActive && !model.MicrophoneActive, "Source toggles not respected");
                model.Microphone = true;
                Check(model.MicrophoneActive, "Microphone not active while recording");
                typeof(MainViewModel).GetProperty(nameof(MainViewModel.IsPaused))!.SetValue(model, true);
                Check(!model.SystemAudioActive && !model.MicrophoneActive, "Paused sources show active recording");
                typeof(MainViewModel).GetProperty(nameof(MainViewModel.IsRecording))!.SetValue(model, false);
                typeof(MainViewModel).GetProperty(nameof(MainViewModel.IsPaused))!.SetValue(model, false);
            }
            finally
            {
                Wait(model.ShutdownAsync());
                SettingsStore.Save(new AppSettings { Language = AppLanguage.Russian, SystemAudio = false, OutputDirectory = _output, Fps = 30 });
            }
        });
    }

    static void RenderScreenMarker()
    {
        var state = new MarkerState();
        state.SetActive(true);
        state.BeginStroke(180, 260, 24);
        state.AddPoint(650, 260);
        Type type = typeof(MainWindow).Assembly.GetType("FrameForge.UI.MarkerWindow")!;
        var window = (Window)Activator.CreateInstance(type, [state, new MonitorInfo { Left = 160, Top = 160, Width = 640, Height = 360 }])!;
        try
        {
            type.GetMethod("ShowOnMonitor")!.Invoke(window, null);
            Wait(Task.Delay(100));
            SaveWindow(window, "screen-marker.png");
            byte[] before = RenderPixels(window, out int width, out _);
            int center = (100 * width + 200) * 4;
            Check(before[center + 2] > 100 && before[center + 1] > 80 && before[center] < 40, "Screen marker yellow ink missing");
            state.SetActive(false);
            window.InvalidateVisual();
            Wait(Task.Delay(40));
            byte[] after = RenderPixels(window, out _, out _);
            Check(after[center + 2] < 5, "Released screen marker remains visible");
        }
        finally { window.Close(); }
    }

    static void RepeatMarkerActivation()
    {
        Type type = typeof(MainWindow).Assembly.GetType("FrameForge.UI.MarkerController")!;
        var state = new MarkerState();
        var controller = (IDisposable)Activator.CreateInstance(type, [state])!;
        var windows = (System.Collections.IList)type.GetField("_windows", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(controller)!;
        void Request(bool active)
        {
            type.GetField("_leftAlt", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(controller, active);
            type.GetMethod("QueueState", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(controller, null);
        }
        void Drain() => Wait(Application.Current.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle).Task);
        try
        {
            // Exercise the controller's input queue without listening to the user's
            // physical keyboard; native hook installation is checked separately.
            type.GetField("_enabled", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(controller, true);
            Request(true);
            Wait(Task.Delay(80));
            Drain();
            Check(windows.Count > 0, "Alt did not create marker windows");
            var previous = (Window)windows[0]!;
            Point origin = previous.PointToScreen(new Point());
            state.BeginStroke((float)origin.X + 60, (float)origin.Y + 100, 24);
            state.AddPoint((float)origin.X + 400, (float)origin.Y + 100);
            Wait(Task.Delay(60));
            Check(CountYellow(RenderPixels(previous, out _, out _)) > 50, "Initial marker surface has no ink");
            Request(false);
            Wait(Task.Delay(40));
            Drain();
            Check(windows.Count == 0 && !previous.IsVisible, "Release retained a window with a stale rendered surface");
            Request(true);
            Wait(Task.Delay(60));
            Drain();
            var next = (Window)windows[0]!;
            Check(!ReferenceEquals(previous, next) && CountYellow(RenderPixels(next, out _, out _)) == 0, "Next Alt activation replayed the old drawing");
            for (int i = 0; i < 3; i++)
            {
                state.BeginStroke((float)origin.X + 60, (float)origin.Y + 100, 24);
                state.AddPoint((float)origin.X + 400, (float)origin.Y + 100);
                previous = (Window)windows[0]!;
                // Release and press before dispatching either update, as with rapid input.
                Request(false);
                Check(state.Snapshot().Segments.Length == 0, "Fast Alt release failed to clear ink immediately");
                Request(true);
                Wait(Task.Delay(50));
                Drain();
                next = (Window)windows[0]!;
                Check(!previous.IsVisible && !ReferenceEquals(previous, next) && state.Snapshot().Segments.Length == 0,
                    "Fast release/press was coalesced and retained stale ink");
            }
            SaveWindow((Window)windows[0]!, "marker-repeated-alt-empty.png");
        }
        finally { controller.Dispose(); }
        Check(windows.Count == 0, "Marker activation leaked windows");
    }

    static void RenderAudioMeters()
    {
        var system = new AudioLevelIndicator { IsActive = true, Level = .85, Margin = new Thickness(14, 0, 0, 0) };
        var mic = new AudioLevelIndicator { IsActive = true, SignalColor = Color.FromRgb(100, 213, 173), Margin = new Thickness(14, 0, 0, 0) };
        var content = new StackPanel { Margin = new Thickness(24) };
        foreach ((string label, AudioLevelIndicator meter) in new[] { (Loc.Get("SystemAudio"), system), (Loc.Get("Microphone"), mic) })
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 8) };
            row.Children.Add(new TextBlock { Text = label, Width = 180, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(meter);
            content.Children.Add(row);
        }
        var window = new Window { Width = 340, Height = 170, WindowStyle = WindowStyle.None, Content = content,
            Background = (Brush)Application.Current.Resources["BgBrush"], Foreground = Brushes.White };
        window.Show();
        try
        {
            Wait(Task.Delay(500));
            double Shown(AudioLevelIndicator meter) => (double)typeof(AudioLevelIndicator).GetField("_shown", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(meter)!;
            Check(Shown(system) > .8 && Shown(mic) == 0, "Meters do not react independently");
            SaveWindow(window, "audio-circles.png");
            system.IsActive = false;
            Wait(Task.Delay(1600));
            Check(Shown(system) == 0 && !(bool)typeof(AudioLevelIndicator).GetField("_subscribed", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(system)!, "Inactive meter did not settle or release animation");
            Check((string)system.ToolTip == Loc.Get("AudioLevelOff") && (string)mic.ToolTip == Loc.Get("AudioLevelQuiet"), "Meter statuses do not distinguish quiet from off");
        }
        finally { window.Close(); }
    }

    static byte[] RenderPixels(Window window, out int width, out int height)
    {
        DpiScale dpi = VisualTreeHelper.GetDpi(window);
        width = (int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX);
        height = (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY);
        var bitmap = new RenderTargetBitmap(width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        return pixels;
    }

    static void RecordMarker(MonitorInfo monitor, EncoderPreference encoder, bool windowCapture)
    {
        var target = new Window { Title = "FrameForge marker capture test", WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            Width = 420, Height = 260, Left = 80, Top = 80, Topmost = true, Background = new SolidColorBrush(Color.FromRgb(24, 32, 48)) };
        target.Show();
        Wait(Task.Delay(150));
        var state = new MarkerState();
        using var engine = new RecordingEngine(state);
        string path = Path.Combine(_output, $"marker-{encoder}-{(windowCapture ? "window" : "region")}.mp4");
        try
        {
            Point origin = target.PointToScreen(new Point());
            var request = windowCapture
                ? new CaptureRequest { Monitor = monitor, Window = new WindowInteropHelper(target).Handle }
                : new CaptureRequest { Monitor = monitor, CropX = ((int)origin.X - monitor.Left + 30) & ~1, CropY = ((int)origin.Y - monitor.Top + 30) & ~1, CropWidth = 320, CropHeight = 180 };
            state.SetActive(true);
            // Cross the entire target, including the center of either capture type.
            state.BeginStroke((float)origin.X + 10, (float)origin.Y + 120, 36);
            state.AddPoint((float)origin.X + 610, (float)origin.Y + 120);
            Wait(Task.Run(() => engine.Start(request, path, new AppSettings { Fps = 30, Encoder = encoder, CaptureCursor = false,
                SystemAudio = false, Microphone = false, TemporaryMarker = true })));
            Wait(Task.Delay(700));
            state.SetActive(false);
            Wait(Task.Delay(250));
            state.SetActive(true);
            Wait(Task.Delay(550));
            Wait(Task.Run(engine.Stop));
            Check(engine.LastResult is { Error: null }, "Marker recording failed: " + engine.LastResult?.Error);
            Mp4Probe before = Mp4Probe.Read(path, 5);
            Mp4Probe after = Mp4Probe.Read(path, 38);
            Check(before.SampleCount >= 38 && before.Width == after.Width && before.Height == after.Height, "Marker recording lost frames or changed dimensions");
            int yellow = CountYellow(before.PreviewPixels);
            Check(yellow > 50, $"Yellow marker missing from encoded {encoder}/{windowCapture} frame ({yellow})");
            Check(CountYellow(after.PreviewPixels) == 0, "Marker remained in video after Alt release");
        }
        finally { Wait(Task.Run(engine.Stop)); target.Close(); }
    }

    static int CountYellow(byte[]? pixels)
    {
        if (pixels == null) return 0;
        int count = 0;
        for (int i = 0; i < pixels.Length; i += 4)
            if (pixels[i + 2] > pixels[i] + 60 && pixels[i + 1] > pixels[i] + 40) count++;
        return count;
    }
}
