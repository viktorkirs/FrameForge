using Vortice.MediaFoundation;

namespace FrameForge.Encoding;

public sealed record StreamTimeline(long Samples, long End100Ns);

/// <summary>Scans encoded timestamps without decoding every frame of a long recording.</summary>
public static class MediaTimelineProbe
{
    public static StreamTimeline Read(string path, bool audio = false)
    {
        MediaFoundationStartup.Ensure();
        using IMFSourceReader reader = MediaFactory.MFCreateSourceReaderFromURL(path, null!);
        int stream = (int)(audio ? SourceReaderIndex.FirstAudioStream : SourceReaderIndex.FirstVideoStream);
        reader.SetStreamSelection((int)SourceReaderIndex.AllStreams, false);
        reader.SetStreamSelection(stream, true);
        long samples = 0, end = 0;
        while (true)
        {
            using IMFSample? sample = reader.ReadSample(stream, (SourceReaderControlFlag)0, out _, out SourceReaderFlag flags, out long timestamp);
            if ((flags & SourceReaderFlag.EndOfStream) != 0) break;
            if (sample == null) continue;
            samples++;
            end = Math.Max(end, timestamp + sample.SampleDuration);
        }
        return new StreamTimeline(samples, end);
    }
}
