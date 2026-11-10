namespace FrameForge.Core;

public sealed class RecordingSnapshot
{
    public static RecordingSnapshot Idle { get; } = new();

    public RecordingState State { get; init; } = RecordingState.Idle;
    public TimeSpan Elapsed { get; init; }
    public long FileBytes { get; init; }
    public long FreeDiskBytes { get; init; }
    public double ActualFps { get; init; }
    public long CapturedFrames { get; init; }
    public long WrittenFrames { get; init; }
    public long RepeatedFrames { get; init; }
    public long DroppedFrames { get; init; }
    public string EncoderName { get; init; } = "";
    public bool HardwareEncoder { get; init; }
    public string? Message { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public string? OutputPath { get; init; }
    public int Bitrate { get; init; }
    public double SystemAudioLevel { get; init; }
    public double MicrophoneLevel { get; init; }

    public string ElapsedText
    {
        get
        {
            if (Elapsed.TotalHours >= 1)
                return Elapsed.ToString(@"h\:mm\:ss");
            return Elapsed.ToString(@"mm\:ss");
        }
    }
}

public sealed class RecordingResult
{
    public long AudioFrames { get; init; }
    public long SystemAudioPackets { get; init; }
    public long MicrophonePackets { get; init; }
    public required string Path { get; init; }
    public required string EncoderName { get; init; }
    public bool HardwareEncoder { get; init; }
    public long WrittenFrames { get; init; }
    public long RepeatedFrames { get; init; }
    public long DroppedFrames { get; init; }
    public long CapturedFrames { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public int Fps { get; init; }
    public string? Warning { get; init; }
    public string? Error { get; init; }
}

public sealed class BgraFrame
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required byte[] Pixels { get; init; }

    public int Stride => Width * 4;
}
