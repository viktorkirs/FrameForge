using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using FrameForge.Capture;
using FrameForge.Core;
using FrameForge.Encoding;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

internal static partial class Program
{
    static int LongRecording(string[] args)
    {
        int seconds = 3600;
        int option = Array.IndexOf(args, "--seconds");
        if (option >= 0 && option + 1 < args.Length) seconds = Math.Clamp(int.Parse(args[option + 1]), 5, 14400);
        MonitorInfo monitor = MonitorEnumerator.Select(MonitorEnumerator.GetMonitors(), null);
        Check(RecordingSafety.AvailableDisk(_output) > 2L * 1024 * 1024 * 1024, "Long recording needs at least 2 GiB free");
        var label = new TextBlock { Text = "FrameForge long recording test", Foreground = Brushes.White, FontSize = 26,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var panel = new Grid { Background = Brushes.DarkSlateBlue };
        panel.Children.Add(label);
        var target = new Window { Title = "FrameForge long recording test", Width = 640, Height = 360, Left = 40, Top = 40,
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, ShowActivated = false, Content = panel };
        target.Show();
        Wait(Task.Delay(150));
        string path = Path.Combine(_output, "hour-recording.mp4");
        string statusPath = Path.Combine(_output, "status.json");
        using var engine = new RecordingEngine();
        using var player = new WasapiPlayerBuilder().Build();
        var signal = new SignalGenerator(48000, 2) { Gain = .001, Frequency = 997, Type = SignalGeneratorType.Sin };
        player.Init(signal.ToWaveProvider());
        var watch = Stopwatch.StartNew();
        var samples = new List<object>();
        long nextLog = 0, lastWritten = -1, maximumPrivate = 0;
        int nextPause = 300, nextResize = 600, pauses = 0, resizes = 0;
        try
        {
            nint hwnd = new WindowInteropHelper(target).Handle;
            Wait(Task.Run(() => engine.Start(new CaptureRequest { Monitor = monitor, Window = hwnd }, path,
                new AppSettings { Fps = 30, Encoder = args.Contains("--software") ? EncoderPreference.Software : EncoderPreference.Hardware, AutoBitrate = false, ManualBitrateMbps = 2,
                    SystemAudio = true, Microphone = false, CaptureCursor = false, TemporaryMarker = true })));
            watch.Restart();
            player.Play();
            while (watch.Elapsed.TotalSeconds < seconds)
            {
                double elapsed = watch.Elapsed.TotalSeconds;
                signal.Gain = elapsed < .3 || elapsed > seconds - .5 ? .001 : 0;
                label.Text = $"FrameForge recording test\n{watch.Elapsed:hh\\:mm\\:ss}";
                panel.Background = (int)elapsed % 2 == 0 ? Brushes.DarkSlateBlue : Brushes.DarkSlateGray;
                Check(engine.IsActive && engine.Snapshot.State != RecordingState.Failed, "Long recording stopped unexpectedly: " + engine.Snapshot.Message);
                if (elapsed >= nextPause)
                {
                    engine.Pause(); Wait(Task.Delay(1200)); engine.Resume();
                    pauses++; nextPause += 300;
                }
                if (elapsed >= nextResize)
                {
                    target.Width = resizes % 2 == 0 ? 800 : 640;
                    target.Height = resizes % 2 == 0 ? 300 : 360;
                    resizes++; nextResize += 600;
                }
                if (elapsed >= nextLog)
                {
                    using Process process = Process.GetCurrentProcess();
                    long memory = process.PrivateMemorySize64;
                    maximumPrivate = Math.Max(maximumPrivate, memory);
                    RecordingSnapshot snapshot = engine.Snapshot;
                    if (lastWritten >= 0) Check(snapshot.WrittenFrames > lastWritten, "Long recording stopped writing frames");
                    lastWritten = snapshot.WrittenFrames;
                    var status = new { State = "recording", WallSeconds = elapsed, TargetSeconds = seconds,
                        MediaSeconds = snapshot.Elapsed.TotalSeconds, snapshot.WrittenFrames, snapshot.DroppedFrames, snapshot.CapturedFrames,
                        PrivateBytes = memory, ManagedBytes = GC.GetTotalMemory(false), Pauses = pauses, Resizes = resizes, File = path };
                    samples.Add(status);
                    File.WriteAllText(statusPath, JsonSerializer.Serialize(status));
                    Console.WriteLine($"LONG {elapsed:F0}/{seconds}s: frames={snapshot.WrittenFrames}, dropped={snapshot.DroppedFrames}, private={memory / 1048576}MiB, paused={pauses}, resized={resizes}");
                    nextLog += 60;
                }
                Wait(Task.Delay(200));
            }
            Wait(Task.Run(engine.Stop));
            RecordingResult result = engine.LastResult!;
            Check(result.Error == null && result.SystemAudioPackets > 0, "Long recording failed: " + result.Error);
            StreamTimeline video = MediaTimelineProbe.Read(path);
            StreamTimeline audio = MediaTimelineProbe.Read(path, audio: true);
            double drift = Math.Abs(video.End100Ns - audio.End100Ns) / 10_000_000d;
            Check(video.Samples == result.WrittenFrames && drift < .15, $"Long recording A/V drift: {drift:F4}s");
            TimeSpan tail = TimeSpan.FromTicks(Math.Max(0, video.End100Ns - TimeSpan.TicksPerSecond * 2));
            Mp4Probe decoded = Mp4Probe.Read(path, 8, seek: tail, maxSamples: 12);
            AudioProbe decodedAudio = AudioProbe.Read(path, seek: tail, maxDuration: TimeSpan.FromSeconds(2));
            Check(decoded.HasVideo && !decoded.LooksBlank && decodedAudio.HasAudio && decodedAudio.Frames > 1000, "Final video/audio samples did not decode");
            var report = new { State = "passed", WallSeconds = watch.Elapsed.TotalSeconds, TargetSeconds = seconds,
                VideoSeconds = video.End100Ns / 10_000_000d, AudioSeconds = audio.End100Ns / 10_000_000d, DriftSeconds = drift,
                result.WrittenFrames, result.DroppedFrames, result.RepeatedFrames, result.SystemAudioPackets, Pauses = pauses, Resizes = resizes,
                Encoder = result.EncoderName, result.HardwareEncoder,
                MaximumPrivateBytes = maximumPrivate, FileBytes = new FileInfo(path).Length, File = path };
            File.WriteAllText(statusPath, JsonSerializer.Serialize(report));
            File.WriteAllText(Path.Combine(_output, "long-recording-result.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine(JsonSerializer.Serialize(report));
            return 0;
        }
        finally
        {
            player.Stop();
            Wait(Task.Run(engine.Stop));
            File.WriteAllText(Path.Combine(_output, "memory-samples.json"), JsonSerializer.Serialize(samples));
            target.Close();
        }
    }
}
