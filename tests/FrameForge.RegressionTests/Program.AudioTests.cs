using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using FrameForge.Capture;
using FrameForge.Core;
using FrameForge.Encoding;
using FrameForge.UI;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

internal static partial class Program
{
    static void RunAudioUnitTests()
    {
        Test("audio mixer sums sources and preserves silent gaps", () =>
        {
            var timeline = new AudioTimeline(2);
            timeline.Begin(1_000_000);
            timeline.AddPacket(0, Enumerable.Repeat(.2f, 960).ToArray(), 1_000_000);
            timeline.AddPacket(1, Enumerable.Repeat(.3f, 960).ToArray(), 1_000_000);
            timeline.AddPacket(0, Enumerable.Repeat(.4f, 960).ToArray(), 1_200_000);
            var pcm = new short[2880];
            timeline.ReadPcm(pcm);
            Check(pcm.Take(960).All(value => Math.Abs(value - 16384) <= 1), "Both sources not mixed");
            Check(pcm.Skip(960).Take(960).All(value => value == 0), "Silence gap was removed");
            Check(pcm.Skip(1920).All(value => Math.Abs(value - 13107) <= 1), "Audio after gap moved in time");
        });
        Test("audio pause excludes late packets from the paused interval", () =>
        {
            var timeline = new AudioTimeline(1);
            timeline.Begin(0);
            timeline.Pause(100_000);
            timeline.Resume(300_000);
            timeline.AddPacket(0, Enumerable.Repeat(.1f, 1920).ToArray(), 50_000);
            timeline.AddPacket(0, Enumerable.Repeat(.9f, 960).ToArray(), 200_000);
            timeline.AddPacket(0, Enumerable.Repeat(.2f, 960).ToArray(), 300_000);
            var pcm = new short[1920];
            timeline.ReadPcm(pcm);
            Check(pcm.Take(480).All(value => value == 0), "Initial silence removed");
            Check(pcm.Skip(480).Take(480).All(value => Math.Abs(value - 3277) <= 1), "Pre-pause tail lost");
            Check(pcm.Skip(960).All(value => Math.Abs(value - 6553) <= 1), "Paused audio leaked or resume has a gap");
        });
        Test("audio mixer limits peaks and discards expired ring buffer data", () =>
        {
            var timeline = new AudioTimeline(2, 1);
            timeline.Begin(0);
            var pcm = new short[960];
            for (int block = 0; block < 220; block++)
            {
                timeline.AddPacket(0, Enumerable.Repeat(1f, 960).ToArray(), block * 100_000L);
                timeline.AddPacket(1, Enumerable.Repeat(1f, 960).ToArray(), block * 100_000L);
                timeline.ReadPcm(pcm);
                Check(pcm.All(value => value == short.MaxValue), "Mixing clipped into a negative value");
            }
            timeline.ReadPcm(pcm);
            Check(pcm.All(value => value == 0), "Old audio replayed after ring buffer wrap");
        });
        Test("compact settings keep all tabs and the Save button inside a small window", TabbedSettings);
    }

    static void TabbedSettings()
    {
        var settings = new AppSettings { SmoothCursor = true, SystemAudio = true, Microphone = true };
        var window = (SettingsWindow)typeof(SettingsWindow).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single().Invoke([settings, MonitorEnumerator.GetMonitors()]);
        window.Width = 480;
        window.Height = 380;
        window.Show();
        try
        {
            var tabs = (TabControl)window.FindName("SettingsTabs");
            Check(tabs.Items.Count == 4, "Thematic tabs missing");
            var save = (Button)window.FindName("SaveButton");
            for (int i = 0; i < tabs.Items.Count; i++)
            {
                tabs.SelectedIndex = i;
                Wait(Task.Delay(40));
                window.UpdateLayout();
                Point top = save.TransformToAncestor(window).Transform(new Point());
                Check(top.Y >= 0 && top.Y + save.ActualHeight <= window.ActualHeight && save.IsVisible, "Save button outside small settings window");
                SaveWindow(window, $"settings-tab-{i}-small.png");
            }
            window.Width = 700;
            window.Height = 580;
            tabs.SelectedIndex = 0;
            Wait(Task.Delay(40));
            SaveWindow(window, "settings-video.png");
            tabs.SelectedIndex = 1;
            Wait(Task.Delay(40));
            SaveWindow(window, "settings-audio.png");
            tabs.SelectedIndex = 3;
            Wait(Task.Delay(40));
            SaveWindow(window, "settings-general.png");
        }
        finally { window.Close(); }
    }

    static void RunAudioCaptureTests()
    {
        MonitorInfo monitor = MonitorEnumerator.Select(MonitorEnumerator.GetMonitors(), null);
        Console.WriteLine($"Audio devices: outputs={AudioDeviceCatalog.GetOutputs().Count}, microphones={AudioDeviceCatalog.GetMicrophones().Count}");
        Test("AAC file contains both mixed test frequencies", () => EncodedAudioMix(monitor));
        Test("system audio reaches MP4 with pause and resume on hardware H.264", () => RecordAudio(monitor, true, false, EncoderPreference.Hardware));
        Test("system audio reaches MP4 on software H.264", () => RecordAudio(monitor, true, false, EncoderPreference.Software));
        Test("microphone reaches MP4 and remains synchronized", () => RecordAudio(monitor, false, true, EncoderPreference.Hardware));
        Test("system audio and microphone record together", () => RecordAudio(monitor, true, true, EncoderPreference.Hardware));
        Test("disabled sources produce a video without an audio track", () =>
        {
            using var engine = new RecordingEngine();
            string path = Path.Combine(_output, "audio-disabled.mp4");
            engine.Start(new CaptureRequest { Monitor = monitor, CropWidth = 640, CropHeight = 360 }, path,
                new AppSettings { Fps = 30, SystemAudio = false, Microphone = false });
            Thread.Sleep(400);
            engine.Stop();
            Check(engine.LastResult is { Error: null, AudioFrames: 0 } && !AudioProbe.Read(path).HasAudio, "Disabled audio still recorded");
        });
        Test("missing microphone reports a start error and the engine can be reused", () =>
        {
            using var engine = new RecordingEngine();
            var request = new CaptureRequest { Monitor = monitor, CropWidth = 640, CropHeight = 360 };
            bool failed = false;
            try
            {
                engine.Start(request, Path.Combine(_output, "audio-missing-device.mp4"),
                    new AppSettings { Fps = 30, SystemAudio = true, Microphone = true, MicrophoneDeviceId = "missing-device-for-regression" });
            }
            catch (InvalidOperationException) { failed = true; }
            Check(failed && !engine.IsActive && !string.IsNullOrWhiteSpace(engine.LastResult?.Error), "Missing microphone silently ignored");
            engine.Start(request, Path.Combine(_output, "after-audio-error.mp4"), new AppSettings { Fps = 30, SystemAudio = false, Microphone = false });
            Thread.Sleep(250);
            engine.Stop();
            Check(engine.LastResult is { Error: null }, "Audio start error poisoned the recording engine");
        });
    }

    static void EncodedAudioMix(MonitorInfo monitor)
    {
        using GraphicsDevice device = GraphicsDevice.CreateForMonitor(new MonitorHandle(monitor.Handle));
        string path = Path.Combine(_output, "audio-synthetic-mix.mp4");
        var timeline = new AudioTimeline(2);
        timeline.Begin(0);
        timeline.AddPacket(0, Tone(440, .12f, AudioTimeline.SampleRate), 0);
        timeline.AddPacket(1, Tone(880, .08f, AudioTimeline.SampleRate), 0);
        using (var writer = Mp4VideoWriter.Open(device.Device, path, 128, 128, 30, 1_000_000, EncoderPreference.Software, out _, audio: true))
        {
            var pixels = Enumerable.Repeat((byte)100, 128 * 128 * 4).ToArray();
            var pcm = new short[1600 * 2];
            for (int i = 0; i < 30; i++)
            {
                writer.WriteBgra(pixels, i * 10_000_000L / 30, (i + 1) * 10_000_000L / 30 - i * 10_000_000L / 30, i == 0);
                timeline.ReadPcm(pcm);
                writer.WriteAudio(MemoryMarshal.AsBytes(pcm.AsSpan()), i * 1600L);
            }
            writer.Finish();
        }
        AudioProbe probe = AudioProbe.Read(path);
        Check(probe.HasAudio && probe.Frames >= 48000 && probe.Rms > .05, "AAC audio missing or silent");
        Check(ToneAmplitude(probe.PreviewMono, 440) > .03 && ToneAmplitude(probe.PreviewMono, 880) > .02, "One mixed source lost in encoded AAC");
    }

    static void RecordAudio(MonitorInfo monitor, bool system, bool microphone, EncoderPreference encoder)
    {
        using var enumerator = new MMDeviceEnumerator();
        using MMDevice output = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        using var player = new WasapiPlayerBuilder().WithDevice(output).Build();
        var signal = new SignalGenerator(AudioTimeline.SampleRate, 2) { Gain = .06, Frequency = 997, Type = SignalGeneratorType.Sin };
        player.Init(signal.ToWaveProvider());
        using var engine = new RecordingEngine();
        string path = Path.Combine(_output, $"audio-{system}-{microphone}-{encoder}.mp4");
        try
        {
            engine.Start(new CaptureRequest { Monitor = monitor, CropWidth = 640, CropHeight = 360 }, path,
                new AppSettings { Fps = 30, Encoder = encoder, SystemAudio = system, Microphone = microphone });
            Thread.Sleep(250);
            if (system) player.Play();
            Thread.Sleep(600);
            RecordingSnapshot active = engine.Snapshot;
            Check(active.State == RecordingState.Recording, "Audio activity snapshot not published");
            if (system) Check(active.SystemAudioLevel > 0, "Real system-audio signal did not reach the indicator");
            else Check(active.SystemAudioLevel == 0, "Disabled system source has activity");
            if (!microphone) Check(active.MicrophoneLevel == 0, "Disabled microphone has activity");
            engine.Pause();
            Check(SpinWait.SpinUntil(() => engine.Snapshot.State == RecordingState.Paused, 3000), "Audio recording did not pause");
            Check(engine.Snapshot.SystemAudioLevel == 0 && engine.Snapshot.MicrophoneLevel == 0, "Paused indicators retained live activity");
            Thread.Sleep(400);
            engine.Resume();
            Thread.Sleep(750);
            engine.Stop();
            player.Stop();
            RecordingResult result = engine.LastResult ?? throw new Exception("No audio recording result");
            Check(result.Error == null, result.Error ?? "");
            AudioProbe audio = AudioProbe.Read(path);
            Mp4Probe video = Mp4Probe.Read(path);
            Check(audio.HasAudio && audio.Frames > 48000 && audio.Channels == 2 && audio.SampleRate == 48000, "Audio stream missing or wrong format");
            double videoSeconds = engine.Snapshot.Elapsed.TotalSeconds;
            Check(Math.Abs(audio.Duration100Ns / 10_000_000d - videoSeconds) < .1, "Audio and video durations drifted");
            Check(videoSeconds < 1.95 && video.HasVideo, "Pause was not removed from the media timeline");
            if (system)
            {
                Check(result.SystemAudioPackets > 0, "No real system-audio packets captured");
                Check(audio.Rms > .00001 && ToneAmplitude(audio.PreviewMono, 997) > .00001, "System test tone did not reach MP4");
            }
            if (microphone) Check(result.MicrophonePackets > 0, "No real microphone packets captured");
            Console.WriteLine($"  audio system={system} mic={microphone} {encoder}: {audio.Frames} frames, RMS={audio.Rms:F6}, packets={result.SystemAudioPackets}/{result.MicrophonePackets}, A/V={audio.Duration100Ns / 10_000_000d:F3}/{videoSeconds:F3}s");
        }
        finally { player.Stop(); engine.Stop(); }
    }

    static float[] Tone(double frequency, float gain, int frames)
    {
        var result = new float[frames * 2];
        for (int i = 0; i < frames; i++)
            result[i * 2] = result[i * 2 + 1] = gain * (float)Math.Sin(i * 2 * Math.PI * frequency / AudioTimeline.SampleRate);
        return result;
    }

    static double ToneAmplitude(float[] signal, double frequency)
    {
        double real = 0, imaginary = 0;
        for (int i = 0; i < signal.Length; i++)
        {
            double angle = i * 2 * Math.PI * frequency / AudioTimeline.SampleRate;
            real += signal[i] * Math.Cos(angle);
            imaginary += signal[i] * Math.Sin(angle);
        }
        return signal.Length == 0 ? 0 : 2 * Math.Sqrt(real * real + imaginary * imaginary) / signal.Length;
    }
}
