using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Numerics;
using System.Xml.Linq;
using FrameForge.Capture;
using FrameForge.Core;
using FrameForge.Encoding;
using FrameForge.UI;

internal static partial class Program
{
    static int _passed;
    static string _output = "";

    [STAThread]
    static int Main(string[] args)
    {
        _output = Path.GetFullPath(args.FirstOrDefault(arg => !arg.StartsWith("--"))
            ?? Path.Combine(AppContext.BaseDirectory, "results"));
        Directory.CreateDirectory(_output);
        // Keep test settings and logs separate from the user's installed application.
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "portable.marker"), "");
        if (args.Contains("--headless"))
        {
            try
            {
                RunAudioTimelineTests();
                RunAuditCoreTests();
                File.WriteAllText(Path.Combine(_output, "regression-result.txt"), $"PASS: {_passed} headless checks");
                Console.WriteLine($"PASS: {_passed} headless checks");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        XElement source = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "ThemeSource.xaml")).Root!;
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var resources = new XElement(presentation + "ResourceDictionary",
            source.Attributes().Where(attribute => attribute.IsNamespaceDeclaration),
            source.Element(presentation + "Application.Resources")!.Elements());
        resources.SetAttributeValue(XNamespace.Xmlns + "local", "clr-namespace:FrameForge.UI;assembly=FrameForge");
        app.Resources = (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(resources.ToString());
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        try
        {
            if (args.Contains("--long-recording")) return LongRecording(args);
            if (args.Contains("--screenshots"))
            {
                RunScreenshotTests();
                string screenshotReport = $"PASS: {_passed} screenshot checks";
                Console.WriteLine(screenshotReport);
                File.WriteAllText(Path.Combine(_output, "regression-result.txt"), screenshotReport + Environment.NewLine);
                return 0;
            }
            if (args.Contains("--preferences"))
            {
                RunPreferencesTests();
                string preferencesReport = $"PASS: {_passed} preferences checks";
                Console.WriteLine(preferencesReport);
                File.WriteAllText(Path.Combine(_output, "regression-result.txt"), preferencesReport + Environment.NewLine);
                return 0;
            }
            RunPreferencesTests();
            Test("hotkeys reject invalid keys and empty segments", () =>
            {
                foreach (string text in new[] { "Ctrl+Ж", "Ctrl+١", "Ctrl++F9", "+F9", "F9+", "Ctrl+", "F25", "A+B" })
                    Check(!Hotkey.TryParse(text, out _), "Accepted invalid hotkey: " + text);
                Check(Hotkey.TryParse(" Shift + CONTROL + f9 ", out Hotkey parsed), "Valid hotkey rejected");
                Check(parsed.VirtualKey == 0x78 && parsed.Modifiers == 0x4006, "Wrong key/modifiers");
            });
            Test("disabled commands ignore direct and hotkey execution", () =>
            {
                int calls = 0;
                bool enabled = false;
                var command = new RelayCommand(() => calls++, () => enabled);
                command.Execute(null);
                Check(calls == 0, "Disabled command executed");
                enabled = true;
                command.Execute(null);
                Check(calls == 1, "Enabled command failed");
            });
            Test("settings normalize and round-trip", () =>
            {
                var settings = new AppSettings { Fps = 99, ManualBitrateMbps = 500, RegionWidth = 321, RegionHeight = 201, Language = AppLanguage.Russian };
                SettingsStore.Save(settings);
                AppSettings loaded = SettingsStore.Load();
                Check(loaded.Fps == 60 && loaded.ManualBitrateMbps == 120, "Settings ranges not normalized");
                Check(loaded.RegionWidth == 320 && loaded.RegionHeight == 200 && loaded.Language == AppLanguage.Russian, "Settings round-trip failed");
                settings.CursorMotion = CursorMotionStyle.Spiral;
                settings.CursorSmoothnessMilliseconds = 1100;
                settings.SystemAudioDeviceId = "output-test";
                settings.MicrophoneDeviceId = "input-test";
                settings.TemporaryMarker = true;
                SettingsStore.Save(settings.Clone());
                loaded = SettingsStore.Load();
                Check(loaded.CursorMotion == CursorMotionStyle.Spiral && loaded.CursorSmoothnessMilliseconds == 1100, "Cursor profile did not round-trip");
                Check(loaded.SystemAudioDeviceId == "output-test" && loaded.MicrophoneDeviceId == "input-test", "Audio device selections did not round-trip");
                Check(loaded.TemporaryMarker, "Temporary marker option did not round-trip");
                SettingsStore.Save(new AppSettings { Language = AppLanguage.Russian, SystemAudio = false, OutputDirectory = _output, Fps = 30 });
            });
            Test("settings reject equivalent duplicate shortcuts", () =>
            {
                Loc.Set(AppLanguage.Russian);
                var constructor = typeof(SettingsWindow).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
                var window = (SettingsWindow)constructor.Invoke([new AppSettings(), Array.Empty<MonitorInfo>()]);
                ((TextBox)window.FindName("StartHotkeyBox")).Text = "Ctrl+Shift+F9";
                ((TextBox)window.FindName("PauseHotkeyBox")).Text = "shift+control+f9";
                typeof(SettingsWindow).GetMethod("Save_Click", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [window, new RoutedEventArgs()]);
                Check(((TextBlock)window.FindName("ErrorText")).Text == Loc.Get("HotkeyDuplicate"), "Duplicate shortcut accepted");
                window.Close();
            });
            Test("parallel settings saves remain valid", () =>
            {
                Parallel.For(0, 40, index =>
                {
                    SettingsStore.Save(new AppSettings { Fps = 30, Language = AppLanguage.Russian, ManualBitrateMbps = index + 1 });
                    AppSettings loaded = SettingsStore.Load();
                    Check(loaded.Language == AppLanguage.Russian && loaded.Fps == 30, "Settings read was incomplete");
                });
                SettingsStore.Save(new AppSettings { Language = AppLanguage.Russian, SystemAudio = false, OutputDirectory = _output, Fps = 30 });
            });
            Test("about text and translations are complete", () =>
            {
                var dictionaries = new[] { "En", "De", "Ru" }.Select(name =>
                    (Dictionary<string, string>)typeof(Loc).GetField(name, BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!).ToArray();
                Check(dictionaries.Skip(1).All(table => table.Keys.ToHashSet().SetEquals(dictionaries[0].Keys)), "Translation keys differ");
                foreach (AppLanguage language in Enum.GetValues<AppLanguage>())
                {
                    Loc.Set(language);
                    var window = new AboutWindow();
                    Check(((TextBlock)window.FindName("VersionText")).Text.Contains(typeof(AboutWindow).Assembly.GetName().Version!.ToString(3)), "About version missing");
                    Check(window.Title == Loc.Get("About"), "About title not translated");
                    window.Close();
                }
                Check(typeof(AboutWindow).Assembly.GetCustomAttribute<AssemblyCompanyAttribute>()?.Company == "Victor Kirsanov", "Manufacturer metadata differs");
            });
            Test("region movement preserves size and stays on a monitor with negative coordinates", () =>
            {
                var monitor = new ScreenRect(-1920, -200, 1920, 1080);
                var region = new ScreenRect(-1800, 100, 640, 360);
                Check(RegionPlacement.MoveWithin(region, 100, 60, monitor) == new ScreenRect(-1700, 160, 640, 360), "Region did not follow movement");
                Check(RegionPlacement.MoveWithin(region, int.MaxValue, int.MaxValue, monitor) == new ScreenRect(-640, 520, 640, 360), "Region escaped bottom/right edge");
                Check(RegionPlacement.MoveWithin(region, int.MinValue, int.MinValue, monitor) == new ScreenRect(-1920, -200, 640, 360), "Region escaped top/left edge");
                ScreenRect aligned = RegionPlacement.MoveWithin(region, 3, 3, monitor);
                Check(((aligned.X - monitor.X) & 1) == 0 && ((aligned.Y - monitor.Y) & 1) == 0, "Crop not aligned for H.264");
            });
            Test("window search combines title and application and ignores case", () =>
            {
                var choice = new WindowChoice { Title = "Отчёт за ноябрь — Google Chrome", ProcessName = "chrome" };
                Check(choice.Matches("CHROME отчёт") && choice.Matches("   ") && choice.Matches(null), "Valid window search failed");
                Check(!choice.Matches("chrome warehouse"), "Search did not require all terms");
            });
            Test("window picker filters cards and reports empty results", () =>
            {
                var windows = new[] { new WindowChoice { Title = "Report", ProcessName = "chrome" }, new WindowChoice { Title = "Notes", ProcessName = "notepad" } };
                var picker = (WindowPicker)typeof(WindowPicker).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single().Invoke([windows]);
                var search = (TextBox)typeof(WindowPicker).GetField("_search", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(picker)!;
                var cards = (WrapPanel)typeof(WindowPicker).GetField("_cards", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(picker)!;
                Check(cards.Children.Count == 2, "Initial window cards missing");
                search.Text = "CHROME";
                Check(cards.Children.Count == 1, "Window filtering failed");
                search.Text = "missing-window";
                var empty = (TextBlock)typeof(WindowPicker).GetField("_empty", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(picker)!;
                Check(cards.Children.Count == 0 && empty.Visibility == Visibility.Visible, "Empty search state missing");
                picker.Close();
            });
            Test("region selector does not resize from an edge extended beyond the selection", () =>
            {
                Type spanType = typeof(RegionWindow).GetNestedType("MonitorSpan", BindingFlags.NonPublic)!;
                object span = Activator.CreateInstance(spanType, [0, 0, 1920, 1080, 96u, 96u])!;
                var selector = (RegionWindow)typeof(RegionWindow).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single().Invoke([new ScreenRect(100, 100, 640, 360), span]);
                MethodInfo hit = typeof(RegionWindow).GetMethod("HitTest", BindingFlags.Instance | BindingFlags.NonPublic, null, [typeof(int), typeof(int)], null)!;
                Check(hit.Invoke(selector, [100, 900])!.ToString() == "Create", "Extended edge intercepted creation");
                Check(hit.Invoke(selector, [300, 200])!.ToString() == "Move", "Inside selection does not move");
                Type dragType = typeof(RegionWindow).GetNestedType("DragKind", BindingFlags.NonPublic)!;
                typeof(RegionWindow).GetMethod("BeginDrag", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(selector, [Enum.Parse(dragType, "Move"), 300, 200]);
                typeof(RegionWindow).GetMethod("MoveSelection", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(selector, [400, 300]);
                Check((int)typeof(RegionWindow).GetField("_x", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(selector)! == 200 &&
                    (int)typeof(RegionWindow).GetField("_y", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(selector)! == 200,
                    "Region did not move before confirmation");
                selector.Close();
            });
            Test("confirmed region movement updates the frame and saves the new coordinates", () =>
            {
                SettingsStore.Save(new AppSettings { Mode = CaptureMode.Region, RegionX = 200, RegionY = 200, RegionWidth = 640, RegionHeight = 360, Language = AppLanguage.Russian, OutputDirectory = _output });
                var model = new MainViewModel();
                object border = typeof(MainViewModel).GetField("_regionBorder", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(model)!;
                object handle = border.GetType().GetField("_handle", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(border)!;
                Check(handle is Window { IsVisible: true }, "Confirmed region has no visible move handle");
                var move = (Func<ScreenRect, bool>)handle.GetType().GetField("_move", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(handle)!;
                var next = new ScreenRect(300, 280, 640, 360);
                Check(move(next), "Confirmed region rejected movement");
                Check((ScreenRect)border.GetType().GetField("_region", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(border)! == next, "Frame did not follow the moved selection");
                ((Action)handle.GetType().GetField("_complete", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(handle)!).Invoke();
                AppSettings saved = SettingsStore.Load();
                Check(saved.RegionX == 300 && saved.RegionY == 280 && saved.RegionWidth == 640 && saved.RegionHeight == 360, "Moved region was not saved");
                Loc.Set(AppLanguage.German);
                Check(((TextBlock)handle.GetType().GetField("_label", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(handle)!).Text.Contains(Loc.Get("MoveRegion")), "Move handle language did not update");
                Loc.Set(AppLanguage.Russian);
                border.GetType().GetMethod("SetMoveEnabled")!.Invoke(border, [false]);
                Check(!move(new ScreenRect(400, 300, 640, 360)), "Disabled handle moved the region");
                Wait(model.ShutdownAsync());
                Check(!((Window)handle).IsVisible, "Move handle remained after shutdown");
                SettingsStore.Save(new AppSettings { Language = AppLanguage.Russian, SystemAudio = false, OutputDirectory = _output, Fps = 30 });
            });
            Test("edit button reopens the current region and preserves it on cancel", EditConfirmedRegion);
            Test("cursor accelerates, brakes and arrives precisely", () =>
            {
                var motion = new SmoothCursorMotion();
                motion.Reset(0, 0);
                motion.Advance(1000, 0, 1d / 120);
                float initialSpeed = motion.Velocity.Length();
                for (int i = 0; i < 11; i++) motion.Advance(1000, 0, 1d / 120);
                Check(motion.Velocity.Length() > initialSpeed * 2, "Cursor has no acceleration");
                for (int i = 0; i < 360; i++) motion.Advance(1000, 0, 1d / 120);
                Check(motion.Position == new Vector2(1000, 0) && motion.Velocity == Vector2.Zero, "Cursor does not settle precisely");
            });
            Test("spiral is curved and slow mode reduces initial speed", () =>
            {
                float[] maxY = new float[2];
                foreach (CursorMotionStyle style in Enum.GetValues<CursorMotionStyle>())
                {
                    var motion = new SmoothCursorMotion(style);
                    motion.Reset(0, 0);
                    for (int i = 0; i < 360; i++)
                    {
                        motion.Advance(1000, 0, 1d / 120);
                        maxY[(int)style] = Math.Max(maxY[(int)style], Math.Abs(motion.Position.Y));
                    }
                }
                Check(maxY[0] > 30 && maxY[1] > maxY[0] * 1.5, "Curve and spiral are indistinguishable");
                var fast = new SmoothCursorMotion(CursorMotionStyle.Curve, 400);
                var slow = new SmoothCursorMotion(CursorMotionStyle.Curve, 1100);
                fast.Reset(0, 0); slow.Reset(0, 0);
                fast.Advance(1000, 0, 0.1); slow.Advance(1000, 0, 0.1);
                Check(slow.Position.X < fast.Position.X * 0.5, "Slow speed is too fast");
            });
            Test("cursor motion is stable across frame rates and target changes", () =>
            {
                var low = new SmoothCursorMotion(CursorMotionStyle.Spiral);
                var high = new SmoothCursorMotion(CursorMotionStyle.Spiral);
                low.Reset(0, 0); high.Reset(0, 0);
                for (int i = 0; i < 30; i++) low.Advance(1000, 200, 1d / 30);
                for (int i = 0; i < 120; i++) high.Advance(1000, 200, 1d / 120);
                Check(Vector2.Distance(low.Position, high.Position) < 0.1f, "Frame rate changes the cursor path");
                Vector2 before = high.Position, velocity = high.Velocity;
                high.Advance(-200, -300, 0);
                Check(high.Position == before && high.Velocity == velocity, "Retargeting jumps or resets inertia");
                high.Advance(-200, -300, 1d / 120);
                Check(Vector2.Distance(high.Position, before) < 30, "Retargeting jumps to the new target");
                high.Advance(float.NaN, 0, 0.1);
                Check(float.IsFinite(high.Position.X) && float.IsFinite(high.Position.Y), "Invalid input poisoned the cursor");
            });
            Test("precise dragging avoids spiral deviation", () =>
            {
                var motion = new SmoothCursorMotion(CursorMotionStyle.Spiral, 1100);
                motion.Reset(0, 0);
                for (int i = 0; i < 120; i++) motion.Advance(400, 0, 1d / 120, precise: true);
                Check(motion.Position == new Vector2(400, 0) && motion.Velocity == Vector2.Zero, "Precise cursor deviates or fails to arrive");
            });
            Test("cursor settings keep choices across language changes and fit a short screen", CursorSettings);
            RunAudioUnitTests();
            RunAuditCoreTests();
            RunMarkerMeterTests();
            Test("audio notice visibility follows toggles", () =>
            {
                var model = new MainViewModel();
                var changed = new List<string?>();
                model.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
                model.SystemAudio = true;
                Check(model.HasAudioNote && changed.Contains(nameof(model.HasAudioNote)), "Audio notice did not appear");
                changed.Clear();
                model.SystemAudio = false;
                Check(!model.HasAudioNote && changed.Contains(nameof(model.HasAudioNote)), "Audio notice did not hide");
                model.Microphone = true;
                Check(model.HasAudioNote, "Microphone notice missing");
                model.Microphone = false;
                Wait(model.ShutdownAsync());
            });
            Test("closing while recording starts waits for final MP4", () =>
            {
                var model = new MainViewModel();
                model.RequestRecord();
                Wait(model.ShutdownAsync());
                var engine = (RecordingEngine)typeof(MainViewModel).GetField("_engine", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(model)!;
                Check(!engine.IsActive && engine.LastResult is { Error: null }, "Engine left active or failed on shutdown");
                Check(File.Exists(engine.LastResult!.Path), "Shutdown recording missing");
            });
            Test("invalid output folder reports an error and allows retry", () =>
            {
                var model = new MainViewModel();
                string blockingFile = Path.Combine(_output, "folder-is-a-file");
                File.WriteAllText(blockingFile, "test");
                model.Settings.OutputDirectory = blockingFile;
                model.RequestRecord();
                Check(!model.IsRecording && model.Hint.Length > 0 && model.RecordCommand.CanExecute(null), "Invalid output folder left recording busy");
                model.Settings.OutputDirectory = _output;
                Wait(model.ShutdownAsync());
            });
            Test("disk estimate uses the configured folder after recording", () =>
            {
                var model = new MainViewModel();
                var engine = (RecordingEngine)typeof(MainViewModel).GetField("_engine", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(model)!;
                typeof(RecordingEngine).GetField("_snapshot", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(engine,
                    new RecordingSnapshot { State = RecordingState.Idle, OutputPath = Path.Combine(_output, "previous", "old.mp4") });
                model.Settings.OutputDirectory = Path.Combine(_output, "next");
                string actual = (string)typeof(MainViewModel).GetMethod("EstimateDirectory", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(model, null)!;
                Check(actual == model.Settings.OutputDirectory, "Disk estimate still uses the old recording drive");
                model.Settings.OutputDirectory = _output;
                Wait(model.ShutdownAsync());
            });
            if (args.Contains("--capture"))
            {
                MonitorInfo monitor = MonitorEnumerator.Select(MonitorEnumerator.GetMonitors(), null);
                Test("live window thumbnails clip to the viewport and release native resources", ThumbnailClipping);
                using var engine = new RecordingEngine();
                Test("hardware capture, pause, resume, screenshot and MP4 decoding", () =>
                    Capture(engine, new CaptureRequest { Monitor = monitor }, EncoderPreference.Hardware, "hardware", pause: true));
                Test("same engine restarts with software region recording", () =>
                    Capture(engine, new CaptureRequest { Monitor = monitor, CropX = 10, CropY = 10, CropWidth = 640, CropHeight = 360 }, EncoderPreference.Software, "software-region", pause: false));
                Test("hardware recording follows region movement without changing video dimensions", () => MovingCapture(monitor, EncoderPreference.Hardware));
                Test("software recording follows region movement without changing video dimensions", () => MovingCapture(monitor, EncoderPreference.Software));
                Test("hardware region recording includes yellow marker and clears it on release", () => RecordMarker(monitor, EncoderPreference.Hardware, false));
                Test("software window recording includes yellow marker and clears it on release", () => RecordMarker(monitor, EncoderPreference.Software, true));
                Test("readback reuses large CPU buffers and clears letterboxing", () => ReusableReadback(monitor));
                Test("GPU resize keeps encoder surfaces and in-flight leases intact", () => ScalerResizeLeases(monitor));
                Test("hardware window capture follows repeated resize without cropping", () => ResizingCapture(monitor, EncoderPreference.Hardware));
                Test("software window capture follows repeated resize without cropping", () => ResizingCapture(monitor, EncoderPreference.Software));
            }
            if (args.Contains("--audio")) RunAudioCaptureTests();
            string report = $"PASS: {_passed} regression checks";
            Console.WriteLine(report);
            File.WriteAllText(Path.Combine(_output, "regression-result.txt"), report + Environment.NewLine);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            File.WriteAllText(Path.Combine(_output, "regression-result.txt"), ex.ToString());
            return 1;
        }
        finally
        {
            app.Shutdown();
        }
    }

    static void Capture(RecordingEngine engine, CaptureRequest request, EncoderPreference encoder, string name, bool pause)
    {
        var settings = new AppSettings { Fps = 30, Resolution = ResolutionPreset.P1080, Encoder = encoder, SystemAudio = false,
            SmoothCursor = true, CursorMotion = CursorMotionStyle.Spiral, HighlightClicks = true };
        string path = Path.Combine(_output, name + ".mp4");
        engine.Start(request, path, settings);
        Thread.Sleep(750);
        BgraFrame frame = engine.CopyCurrentFrame() ?? throw new Exception("No screenshot frame");
        Check(frame.Pixels.Length == frame.Width * frame.Height * 4, "Invalid screenshot size");
        if (pause)
        {
            engine.Pause();
            SpinWait.SpinUntil(() => engine.Snapshot.State == RecordingState.Paused, 3000);
            Check(engine.Snapshot.State == RecordingState.Paused, "Pause failed");
            long written = engine.Snapshot.WrittenFrames;
            Thread.Sleep(400);
            Check(engine.Snapshot.WrittenFrames == written, "Frames written during pause");
            engine.Resume();
        }
        Thread.Sleep(750);
        engine.Stop();
        RecordingResult result = engine.LastResult ?? throw new Exception("No recording result");
        Check(result.Error == null, result.Error ?? "");
        Check(result.HardwareEncoder == (encoder == EncoderPreference.Hardware), "Wrong encoder selected");
        Mp4Probe probe = Mp4Probe.Read(path);
        (int width, int height) = OutputSize.Compute(request.HasCrop ? request.CropWidth : request.Monitor.Width,
            request.HasCrop ? request.CropHeight : request.Monitor.Height, settings.Resolution);
        Check(probe.HasVideo && probe.SampleCount >= 25 && !probe.LooksBlank, "Invalid or blank MP4");
        Check((probe.Width, probe.Height) == (width, height), "Wrong output dimensions");
        Console.WriteLine($"  {name}: {probe.Width}x{probe.Height}, {probe.SampleCount} frames, hardware={result.HardwareEncoder}");
    }

    static void Wait(Task task)
    {
        var frame = new DispatcherFrame();
        task.ContinueWith(_ => Application.Current.Dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
        Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }

    static void MovingCapture(MonitorInfo monitor, EncoderPreference encoder)
    {
        var red = new Border { Background = Brushes.Red };
        var blue = new Border { Background = Brushes.Blue };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(blue, 1);
        grid.Children.Add(red);
        grid.Children.Add(blue);
        var target = new Window { Title = "FrameForge movement test", WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            Width = 800, Height = 400, Left = 80, Top = 80, Topmost = true, Content = grid };
        target.Show();
        Wait(Task.Delay(200));
        try
        {
            Point first = red.PointToScreen(new Point(30, 30));
            Point second = blue.PointToScreen(new Point(30, 30));
            int startX = ((int)first.X - monitor.Left) & ~1;
            int startY = ((int)first.Y - monitor.Top) & ~1;
            int endX = ((int)second.X - monitor.Left) & ~1;
            int endY = ((int)second.Y - monitor.Top) & ~1;
            string path = Path.Combine(_output, $"moving-{encoder}.mp4");
            using var engine = new RecordingEngine();
            Wait(Task.Run(() => engine.Start(new CaptureRequest { Monitor = monitor, CropX = startX, CropY = startY, CropWidth = 128, CropHeight = 128 }, path,
                new AppSettings { Fps = 30, Encoder = encoder, CaptureCursor = false })));
            Wait(Task.Delay(600));
            Check(engine.TryMoveRegion(endX, endY), "Live movement rejected");
            Wait(Task.Delay(800));
            Wait(Task.Run(engine.Stop));
            Check(engine.LastResult is { Error: null }, "Moving recording failed");
            Check(!engine.TryMoveRegion(startX, startY), "Stopped recording accepted live movement");
            Mp4Probe before = Mp4Probe.Read(path, 5);
            Mp4Probe after = Mp4Probe.Read(path, 35);
            Check(before.Width == 128 && before.Height == 128 && after.SampleCount >= 35, "Video dimensions changed or frames were lost");
            Check(before.PreviewPixels is { Length: > 8 } && after.PreviewPixels is { Length: > 8 }, "Movement test frames did not decode");
            Check(before.PreviewPixels![2] > before.PreviewPixels[0] + 100, "Video did not start at red region");
            Check(after.PreviewPixels![0] > after.PreviewPixels[2] + 100, "Encoded video did not move to blue region");
        }
        finally { target.Close(); }
    }

    static void ThumbnailClipping()
    {
        var target = new Window { Title = "FrameForge thumbnail test", Width = 400, Height = 250, Background = Brushes.CornflowerBlue };
        target.Show();
        Wait(Task.Delay(100));
        nint hwnd = new WindowInteropHelper(target).Handle;
        var windows = Enumerable.Range(0, 18).Select(index => new WindowChoice { Handle = hwnd, Title = "Test window " + index, ProcessName = "test" }).ToArray();
        var picker = (WindowPicker)typeof(WindowPicker).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single().Invoke([windows]);
        picker.Height = 600;
        picker.Show();
        Wait(Task.Delay(150));
        try
        {
            var scroll = (ScrollViewer)typeof(WindowPicker).GetField("_scroll", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(picker)!;
            scroll.ScrollToVerticalOffset(190);
            Wait(Task.Delay(150));
            object[] thumbnails = ((System.Collections.IEnumerable)typeof(WindowPicker).GetField("_thumbnails", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(picker)!).Cast<object>().ToArray();
            Point origin = scroll.TransformToAncestor(picker).Transform(new Point());
            double scale = VisualTreeHelper.GetDpi(picker).DpiScaleY;
            int top = (int)Math.Round(origin.Y * scale);
            int bottom = (int)Math.Round((origin.Y + scroll.ActualHeight) * scale);
            int visible = 0;
            foreach (object thumbnail in thumbnails)
            {
                object? props = thumbnail.GetType().GetField("_last", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(thumbnail);
                if (props == null || !(bool)props.GetType().GetField("Visible")!.GetValue(props)!)
                    continue;
                visible++;
                object rect = props.GetType().GetField("Destination")!.GetValue(props)!;
                int rectTop = (int)rect.GetType().GetField("Top")!.GetValue(rect)!;
                int rectBottom = (int)rect.GetType().GetField("Bottom")!.GetValue(rect)!;
                Check(rectTop >= top && rectBottom <= bottom, "Thumbnail overflowed into the header or footer");
            }
            Check(visible > 0 && visible < thumbnails.Length, "No live thumbnails or hidden cards still rendered");
            picker.Close();
            Check(thumbnails.All(thumbnail => (nint)thumbnail.GetType().GetField("_thumbnail", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(thumbnail)! == 0), "Native thumbnail handles leaked");
        }
        finally { if (picker.IsVisible) picker.Close(); target.Close(); }
    }

    static void EditConfirmedRegion()
    {
        SettingsStore.Save(new AppSettings { Mode = CaptureMode.Region, RegionX = 200, RegionY = 200, RegionWidth = 640, RegionHeight = 360, Language = AppLanguage.Russian, OutputDirectory = _output });
        var owner = new Window { Width = 300, Height = 200, Title = "FrameForge edit test" };
        Application.Current.MainWindow = owner;
        owner.Show();
        var model = new MainViewModel();
        object border = typeof(MainViewModel).GetField("_regionBorder", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(model)!;
        object handle = border.GetType().GetField("_handle", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(border)!;
        var edit = (Button)handle.GetType().GetField("_edit", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(handle)!;
        Exception? failure = null;
        try
        {
            Check(edit.IsEnabled, "Edit button disabled before recording");
            ((Window)handle).UpdateLayout();
            SaveWindow((Window)handle, "region-toolbar.png");
            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                RegionWindow? selector = Application.Current.Windows.OfType<RegionWindow>().SingleOrDefault();
                try
                {
                    Check(selector != null, "Edit button did not open the region selector");
                    var pending = (ScreenRect)typeof(RegionWindow).GetField("_pending", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(selector)!;
                    Check(pending == new ScreenRect(200, 200, 640, 360), "Edit did not reopen the current selection");
                    typeof(RegionWindow).GetField("_width", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(selector, 800);
                    typeof(RegionWindow).GetField("_height", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(selector, 450);
                    typeof(RegionWindow).GetMethod("Confirm", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(selector, null);
                }
                catch (Exception ex) { failure = ex; selector?.Close(); }
            }, DispatcherPriority.ApplicationIdle);
            edit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (failure != null) throw failure;
            Check(model.Settings.RegionWidth == 800 && model.Settings.RegionHeight == 450 && ((Window)handle).IsVisible, "New size not applied or toolbar not restored");
            Application.Current.Dispatcher.BeginInvoke(() => Application.Current.Windows.OfType<RegionWindow>().Single().Close(), DispatcherPriority.ApplicationIdle);
            edit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(model.Settings.RegionWidth == 800 && model.Settings.RegionHeight == 450 && ((Window)handle).IsVisible, "Cancel lost the confirmed selection");
            typeof(MainViewModel).GetProperty(nameof(MainViewModel.IsRecording))!.SetValue(model, true);
            typeof(MainViewModel).GetMethod("RaiseCommands", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(model, null);
            Check(!edit.IsEnabled && ((Window)handle).IsEnabled, "Recording must block resizing while keeping movement available");
            typeof(MainViewModel).GetProperty(nameof(MainViewModel.IsRecording))!.SetValue(model, false);
        }
        finally
        {
            Wait(model.ShutdownAsync());
            owner.Close();
            SettingsStore.Save(new AppSettings { Language = AppLanguage.Russian, SystemAudio = false, OutputDirectory = _output, Fps = 30 });
        }
    }

    static void CursorSettings()
    {
        var window = (SettingsWindow)typeof(SettingsWindow).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single().Invoke([new AppSettings { SmoothCursor = true,
            SystemAudio = true, Microphone = true, SystemAudioDeviceId = "missing-output", MicrophoneDeviceId = "missing-input" }, Array.Empty<MonitorInfo>()]);
        var motion = (ComboBox)window.FindName("CursorMotionBox");
        ((TabControl)window.FindName("SettingsTabs")).SelectedItem = window.FindName("CursorTab");
        var speed = (ComboBox)window.FindName("CursorSpeedBox");
        ((CheckBox)window.FindName("TemporaryMarkerBox")).IsChecked = true;
        motion.SelectedValue = CursorMotionStyle.Spiral;
        speed.SelectedValue = 1100;
        Loc.Set(AppLanguage.German);
        Check((CursorMotionStyle)motion.SelectedValue == CursorMotionStyle.Spiral && (int)speed.SelectedValue == 1100, "Translation reset cursor choices");
        Loc.Set(AppLanguage.Russian);
        window.MaxHeight = 560;
        Exception? failure = null;
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            try
            {
                window.UpdateLayout();
                Check(window.ActualHeight <= 560, "Settings overflow a short screen");
                motion.BringIntoView();
                window.UpdateLayout();
                SaveWindow(window, "cursor-settings.png");
                typeof(SettingsWindow).GetMethod("Save_Click", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [window, new RoutedEventArgs()]);
            }
            catch (Exception ex) { failure = ex; window.Close(); }
        }, DispatcherPriority.ApplicationIdle);
        Check(window.ShowDialog() == true, "Settings did not save");
        if (failure != null) throw failure;
        var saved = (AppSettings)typeof(SettingsWindow).GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        Check(saved.CursorMotion == CursorMotionStyle.Spiral && saved.CursorSmoothnessMilliseconds == 1100, "Cursor choices not saved");
        Check(saved.TemporaryMarker, "Temporary marker checkbox not saved");
        Check(saved.SystemAudio && saved.Microphone && saved.SystemAudioDeviceId == "missing-output" && saved.MicrophoneDeviceId == "missing-input", "Audio device choices were silently reset");
    }

    static void SaveWindow(Window window, string name)
    {
        DpiScale dpi = VisualTreeHelper.GetDpi(window);
        var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX)),
            Math.Max(1, (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY)), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(_output, name));
        png.Save(stream);
    }

    static void Test(string name, Action action)
    {
        action();
        _passed++;
        Console.WriteLine("PASS " + name);
    }

    static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

}
