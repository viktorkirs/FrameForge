using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FrameForge.Capture;
using FrameForge.Core;
using FrameForge.Encoding;

namespace FrameForge.UI;

static class SelfTest
{
    public static int Run(string[] args)
    {
        int seconds = 3;
        string outputDirectory = Path.Combine(AppContext.BaseDirectory, "artifacts", "selftest");
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--seconds" && i + 1 < args.Length && int.TryParse(args[i + 1], out int parsed))
                seconds = Math.Clamp(parsed, 1, 30);
            if (args[i] == "--output" && i + 1 < args.Length)
                outputDirectory = args[i + 1];
        }

        string reportPath = Path.Combine(outputDirectory, "result.txt");
        try
        {
            Directory.CreateDirectory(outputDirectory);
            File.WriteAllText(reportPath, "started\n");
            IReadOnlyList<MonitorInfo> monitors = MonitorEnumerator.GetMonitors();
            MonitorInfo monitor = MonitorEnumerator.Select(monitors, null);
            string videoPath = Path.Combine(outputDirectory, "selftest.mp4");
            if (File.Exists(videoPath))
                File.Delete(videoPath);

            var settings = new AppSettings
            {
                Fps = 30,
                Resolution = monitor.Width > 1920 || monitor.Height > 1080 ? ResolutionPreset.P1080 : ResolutionPreset.Original,
                Quality = VideoQuality.High,
                Encoder = EncoderPreference.Auto,
                CaptureCursor = true,
                AutoBitrate = true
            };
            (int width, int height) = OutputSize.Compute(monitor.Width, monitor.Height, settings.Resolution);
            Console.WriteLine($"Monitor {monitor.Label} {monitor.Width}x{monitor.Height} -> {width}x{height} @ {settings.Fps}");

            using var engine = new RecordingEngine();
            engine.Start(monitor, videoPath, settings);
            Thread.Sleep(TimeSpan.FromSeconds(seconds));
            engine.Stop();
            RecordingResult result = engine.LastResult ?? throw new InvalidOperationException("Нет результата записи.");
            if (result.Error != null)
                throw new InvalidOperationException(result.Error);

            Mp4Probe probe = Mp4Probe.Read(videoPath);
            AudioProbe audio = AudioProbe.Read(videoPath);
            int expected = settings.Fps * seconds;
            string report = string.Join(Environment.NewLine,
                $"Encoder: {result.EncoderName} hardware={result.HardwareEncoder}",
                $"Written={result.WrittenFrames} repeated={result.RepeatedFrames} dropped={result.DroppedFrames} captured={result.CapturedFrames}",
                $"File={new FileInfo(videoPath).Length} bytes samples={probe.SampleCount} size={probe.Width}x{probe.Height} luma={probe.LumaAverage:0.0} spread={probe.LumaSpread:0.0}",
                $"Audio={audio.HasAudio} frames={audio.Frames} duration={audio.Duration100Ns / 10_000_000d:0.000}s RMS={audio.Rms:0.000000} packets={result.SystemAudioPackets}/{result.MicrophonePackets}",
                $"Warning: {result.Warning}");
            Console.WriteLine(report);
            File.AppendAllText(reportPath, report + Environment.NewLine);

            if (!probe.HasVideo || probe.SampleCount < expected * 0.6)
                throw new InvalidOperationException($"Слишком мало кадров: {probe.SampleCount}, ожидалось около {expected}.");
            if (probe.Width != width || probe.Height != height)
                throw new InvalidOperationException($"Размер файла {probe.Width}x{probe.Height}, ожидался {width}x{height}.");
            if (probe.LooksBlank)
                throw new InvalidOperationException("Декодированный кадр выглядит пустым.");
            if ((settings.SystemAudio || settings.Microphone) && (!audio.HasAudio || audio.Frames < seconds * AudioTimeline.SampleRate * 0.6))
                throw new InvalidOperationException("Нет звуковой дорожки или слишком мало аудиоданных.");
            if (!result.HardwareEncoder)
                throw new InvalidOperationException("На этой машине ожидался аппаратный кодировщик. " + result.Warning);
            if (result.DroppedFrames * 5 > result.WrittenFrames)
                throw new InvalidOperationException($"Слишком много пропущенных кадров: {result.DroppedFrames} при {result.WrittenFrames} записанных.");
            if (probe.PreviewPixels != null)
            {
                var bitmap = BitmapSource.Create(probe.PreviewWidth, probe.PreviewHeight, 96, 96, PixelFormats.Bgra32, null, probe.PreviewPixels, probe.PreviewWidth * 4);
                var png = new PngBitmapEncoder();
                png.Frames.Add(BitmapFrame.Create(bitmap));
                using var image = File.Create(Path.Combine(outputDirectory, "preview.png"));
                png.Save(image);
            }

            string ok = "SELF-TEST OK " + videoPath;
            Console.WriteLine(ok);
            File.AppendAllText(reportPath, ok + Environment.NewLine);
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("SELF-TEST FAILED");
            Console.WriteLine(ex);
            try
            {
                Directory.CreateDirectory(outputDirectory);
                File.AppendAllText(reportPath, "SELF-TEST FAILED" + Environment.NewLine + ex + Environment.NewLine);
            }
            catch
            {
            }
            return 1;
        }
    }
}
