using System.Text.Json.Serialization;

namespace FrameForge.Core;

public enum CaptureMode
{
    Monitor = 0,
    Window = 1,
    Region = 2
}

public enum ResolutionPreset
{
    Original = 0,
    P1080 = 1,
    P1440 = 2,
    P4K = 3
}

public enum VideoQuality
{
    Low = 0,
    Medium = 1,
    High = 2,
    Ultra = 3
}

public enum EncoderPreference
{
    Auto = 0,
    Hardware = 1,
    Software = 2
}

public enum RecordingState
{
    Idle = 0,
    Starting = 1,
    Recording = 2,
    Paused = 3,
    Stopping = 4,
    Failed = 5
}

public enum CursorMotionStyle
{
    Curve = 0,
    Spiral = 1
}

public sealed class MonitorInfo
{
    public int Index { get; init; }
    public bool IsPrimary { get; init; }
    public nint Handle { get; init; }
    public string DeviceName { get; init; } = "";
    public string AdapterName { get; init; } = "";
    public int Width { get; init; }
    public int Height { get; init; }
    public int Left { get; init; }
    public int Top { get; init; }

    public string Label => IsPrimary
        ? Loc.Format("MonitorDesktop", Index)
        : Loc.Format("MonitorOnly", Index);

    public string ResolutionText => $"{Width} × {Height}";
}

public sealed class AppSettings
{
    public CaptureMode Mode { get; set; } = CaptureMode.Monitor;
    public string? MonitorDeviceName { get; set; }
    public int Fps { get; set; } = 60;
    public ResolutionPreset Resolution { get; set; } = ResolutionPreset.Original;
    public VideoQuality Quality { get; set; } = VideoQuality.High;
    public bool AutoBitrate { get; set; } = true;
    public int ManualBitrateMbps { get; set; } = 20;
    public EncoderPreference Encoder { get; set; } = EncoderPreference.Auto;
    public bool CaptureCursor { get; set; } = true;
    public bool HighlightClicks { get; set; }
    public bool SmoothCursor { get; set; }
    public bool TemporaryMarker { get; set; }
    public CursorMotionStyle CursorMotion { get; set; } = CursorMotionStyle.Curve;
    public int CursorSmoothnessMilliseconds { get; set; } = 700;
    public bool SystemAudio { get; set; } = true;
    public bool Microphone { get; set; }
    public string? SystemAudioDeviceId { get; set; }
    public string? MicrophoneDeviceId { get; set; }
    public string? OutputDirectory { get; set; }
    public bool MinimizeOnRecord { get; set; }
    public bool CheckUpdatesOnStartup { get; set; } = true;
    public string StartStopHotkey { get; set; } = "Ctrl+Shift+F9";
    public string PauseHotkey { get; set; } = "Ctrl+Shift+F10";
    public string ScreenshotHotkey { get; set; } = "Ctrl+Shift+F11";
    public AppLanguage? Language { get; set; }
    public int RegionX { get; set; }
    public int RegionY { get; set; }
    public int RegionWidth { get; set; }
    public int RegionHeight { get; set; }

    public AppSettings Clone() => new()
    {
        Mode = Mode,
        MonitorDeviceName = MonitorDeviceName,
        Fps = Fps,
        Resolution = Resolution,
        Quality = Quality,
        AutoBitrate = AutoBitrate,
        ManualBitrateMbps = ManualBitrateMbps,
        Encoder = Encoder,
        CaptureCursor = CaptureCursor,
        HighlightClicks = HighlightClicks,
        SmoothCursor = SmoothCursor,
        TemporaryMarker = TemporaryMarker,
        CursorMotion = CursorMotion,
        CursorSmoothnessMilliseconds = CursorSmoothnessMilliseconds,
        SystemAudio = SystemAudio,
        Microphone = Microphone,
        SystemAudioDeviceId = SystemAudioDeviceId,
        MicrophoneDeviceId = MicrophoneDeviceId,
        OutputDirectory = OutputDirectory,
        MinimizeOnRecord = MinimizeOnRecord,
        CheckUpdatesOnStartup = CheckUpdatesOnStartup,
        StartStopHotkey = StartStopHotkey,
        PauseHotkey = PauseHotkey,
        ScreenshotHotkey = ScreenshotHotkey,
        Language = Language,
        RegionX = RegionX,
        RegionY = RegionY,
        RegionWidth = RegionWidth,
        RegionHeight = RegionHeight
    };

    public void Normalize()
    {
        if (Fps is not (15 or 24 or 30 or 60 or 120))
            Fps = 60;
        if (ManualBitrateMbps < 1)
            ManualBitrateMbps = 1;
        if (ManualBitrateMbps > 120)
            ManualBitrateMbps = 120;
        if (!Enum.IsDefined(Resolution))
            Resolution = ResolutionPreset.Original;
        if (!Enum.IsDefined(Quality))
            Quality = VideoQuality.High;
        if (!Enum.IsDefined(Encoder))
            Encoder = EncoderPreference.Auto;
        if (!Enum.IsDefined(Mode))
            Mode = CaptureMode.Monitor;
        if (!Enum.IsDefined(CursorMotion))
            CursorMotion = CursorMotionStyle.Curve;
        if (CursorSmoothnessMilliseconds is not (400 or 700 or 1100))
            CursorSmoothnessMilliseconds = 700;
        if (RegionWidth < 16 || RegionHeight < 16)
        {
            RegionX = 0;
            RegionY = 0;
            RegionWidth = 0;
            RegionHeight = 0;
        }
        else
        {
            RegionWidth &= ~1;
            RegionHeight &= ~1;
        }
        if (Language is AppLanguage language && !Enum.IsDefined(language))
            Language = null;
        SystemAudioDeviceId = string.IsNullOrWhiteSpace(SystemAudioDeviceId) ? null : SystemAudioDeviceId.Trim();
        MicrophoneDeviceId = string.IsNullOrWhiteSpace(MicrophoneDeviceId) ? null : MicrophoneDeviceId.Trim();
        StartStopHotkey = string.IsNullOrWhiteSpace(StartStopHotkey) ? "Ctrl+Shift+F9" : StartStopHotkey.Trim();
        PauseHotkey = string.IsNullOrWhiteSpace(PauseHotkey) ? "Ctrl+Shift+F10" : PauseHotkey.Trim();
        ScreenshotHotkey = string.IsNullOrWhiteSpace(ScreenshotHotkey) ? "Ctrl+Shift+F11" : ScreenshotHotkey.Trim();
    }

    [JsonIgnore]
    public string QualityLine
    {
        get
        {
            string resolution = Resolution switch
            {
                ResolutionPreset.P1080 => "1080p",
                ResolutionPreset.P1440 => "1440p",
                ResolutionPreset.P4K => "4K",
                _ => Loc.Get("Original")
            };
            return $"{resolution} · {Fps} FPS · H.264";
        }
    }
}

public readonly record struct ScreenRect(int X, int Y, int Width, int Height)
{
    public bool IsEmpty => Width < 16 || Height < 16;
}

public sealed class CaptureRequest
{
    public required MonitorInfo Monitor { get; init; }
    public nint Window { get; init; }
    public int CropX { get; init; }
    public int CropY { get; init; }
    public int CropWidth { get; init; }
    public int CropHeight { get; init; }
    public bool HasCrop => CropWidth >= 16 && CropHeight >= 16;
}

public static class OutputSize
{
    public static (int Width, int Height) Compute(int sourceWidth, int sourceHeight, ResolutionPreset preset)
    {
        int width = Math.Max(2, sourceWidth & ~1);
        int height = Math.Max(2, sourceHeight & ~1);
        if (preset == ResolutionPreset.Original)
            return (width, height);

        (int maxWidth, int maxHeight) = preset switch
        {
            ResolutionPreset.P1080 => (1920, 1080),
            ResolutionPreset.P1440 => (2560, 1440),
            ResolutionPreset.P4K => (3840, 2160),
            _ => (width, height)
        };

        if (width <= maxWidth && height <= maxHeight)
            return (width, height);

        double scale = Math.Min(maxWidth / (double)width, maxHeight / (double)height);
        int fittedWidth = Math.Max(2, (int)Math.Round(width * scale) & ~1);
        int fittedHeight = Math.Max(2, (int)Math.Round(height * scale) & ~1);
        return (fittedWidth, fittedHeight);
    }
}

public static class VideoBitrate
{
    public static int Compute(VideoQuality quality, int width, int height, int fps)
    {
        double pixelScale = (width * (double)height) / (1920d * 1080d);
        double fpsScale = fps / 60d;
        int baseMbps = quality switch
        {
            VideoQuality.Low => 8,
            VideoQuality.Medium => 16,
            VideoQuality.High => 28,
            VideoQuality.Ultra => 50,
            _ => 16
        };

        int bits = (int)(baseMbps * 1_000_000d * Math.Max(0.35, pixelScale) * Math.Max(0.45, fpsScale));
        return Math.Clamp(bits, 1_000_000, 120_000_000);
    }
}

public readonly record struct Hotkey(uint Modifiers, uint VirtualKey, string Text)
{
    public static bool TryParse(string? text, out Hotkey hotkey)
    {
        hotkey = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        uint modifiers = 0x4000; // MOD_NOREPEAT
        uint key = 0;
        foreach (string raw in text.Split('+', StringSplitOptions.TrimEntries))
        {
            switch (raw.ToUpperInvariant())
            {
                case "CTRL":
                case "CONTROL":
                    modifiers |= 0x0002;
                    break;
                case "SHIFT":
                    modifiers |= 0x0004;
                    break;
                case "ALT":
                    modifiers |= 0x0001;
                    break;
                case "WIN":
                    modifiers |= 0x0008;
                    break;
                default:
                    if (key != 0 || !TryParseKey(raw, out key))
                        return false;
                    break;
            }
        }

        if (key == 0)
            return false;

        hotkey = new Hotkey(modifiers, key, text.Trim());
        return true;
    }

    static bool TryParseKey(string text, out uint key)
    {
        key = 0;
        if (text.Length == 1 && char.IsAsciiLetterOrDigit(text[0]))
        {
            key = char.ToUpperInvariant(text[0]);
            return true;
        }

        if (text.Length is >= 2 and <= 3 && (text[0] == 'F' || text[0] == 'f') && int.TryParse(text.AsSpan(1), out int function) && function is >= 1 and <= 24)
        {
            key = (uint)(0x70 + function - 1);
            return true;
        }

        return false;
    }
}
