using FrameForge.Core;
using SharpGen.Runtime;
using Vortice.MediaFoundation;

namespace FrameForge.Encoding;

/// <summary>A bounded peak envelope, indexed by source time, rather than all decoded PCM samples.</summary>
public sealed record AudioWaveform(bool HasAudio, float[] Peaks)
{
    public static unsafe AudioWaveform Read(string path, TimeSpan duration, CancellationToken cancellationToken = default, int buckets = 4096)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(buckets);
        if (duration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(duration));
        cancellationToken.ThrowIfCancellationRequested();
        MediaFoundationStartup.Ensure();
        using IMFSourceReader reader = MediaFactory.MFCreateSourceReaderFromURL(path, null!);
        try { using IMFMediaType native = reader.GetNativeMediaType((int)SourceReaderIndex.FirstAudioStream, 0); }
        catch (SharpGenException) { return new(false, []); }
        reader.SetStreamSelection((int)SourceReaderIndex.AllStreams, false);
        reader.SetStreamSelection((int)SourceReaderIndex.FirstAudioStream, true);
        using IMFMediaType output = MediaFactory.MFCreateMediaType();
        output.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio).CheckError();
        output.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Pcm).CheckError();
        output.Set(MediaTypeAttributeKeys.AudioNumChannels, 2u).CheckError();
        output.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)AudioTimeline.SampleRate).CheckError();
        output.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u).CheckError();
        output.Set(MediaTypeAttributeKeys.AudioBlockAlignment, 4u).CheckError();
        output.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, (uint)(AudioTimeline.SampleRate * 4)).CheckError();
        reader.SetCurrentMediaType((int)SourceReaderIndex.FirstAudioStream, output);
        var peaks = new float[buckets];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using IMFSample? sample = reader.ReadSample((int)SourceReaderIndex.FirstAudioStream, 0, out _, out SourceReaderFlag flags, out long timestamp);
            if ((flags & SourceReaderFlag.EndOfStream) != 0) break;
            if (sample == null) continue;
            using IMFMediaBuffer buffer = sample.ConvertToContiguousBuffer();
            buffer.Lock(out nint pointer, out _, out int length);
            try
            {
                ReadOnlySpan<short> pcm = new((void*)pointer, length / 2);
                for (int frame = 0; frame < pcm.Length / 2; frame++)
                {
                    long time = timestamp + frame * TimeSpan.TicksPerSecond / AudioTimeline.SampleRate;
                    int bucket = (int)Math.Clamp(time / (double)duration.Ticks * buckets, 0, buckets - 1);
                    float peak = Math.Max(Math.Abs((int)pcm[frame * 2]), Math.Abs((int)pcm[frame * 2 + 1])) / 32768f;
                    peaks[bucket] = Math.Max(peaks[bucket], peak);
                }
            }
            finally { buffer.Unlock(); }
        }
        return new(true, peaks);
    }
}
