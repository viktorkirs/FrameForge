using SharpGen.Runtime;
using Vortice.MediaFoundation;
using FrameForge.Core;

namespace FrameForge.Encoding;

public sealed class AudioProbe
{
    public bool HasAudio { get; init; }
    public int SampleRate { get; init; }
    public int Channels { get; init; }
    public long Frames { get; init; }
    public long Duration100Ns { get; init; }
    public double Rms { get; init; }
    public double Peak { get; init; }
    public float[] PreviewMono { get; init; } = [];

    public static unsafe AudioProbe Read(string path)
    {
        MediaFoundationStartup.Ensure();
        using IMFSourceReader reader = MediaFactory.MFCreateSourceReaderFromURL(path, null!);
        IMFMediaType native;
        try { native = reader.GetNativeMediaType((int)SourceReaderIndex.FirstAudioStream, 0); }
        catch (SharpGenException) { return new AudioProbe(); }
        using (native)
        {
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
            long frames = 0, duration = 0;
            double sumSquares = 0, peak = 0;
            var preview = new List<float>();
            while (true)
            {
                using IMFSample? sample = reader.ReadSample((int)SourceReaderIndex.FirstAudioStream, (SourceReaderControlFlag)0,
                    out _, out SourceReaderFlag flags, out long timestamp);
                if (flags.HasFlag(SourceReaderFlag.EndOfStream)) break;
                if (sample == null) continue;
                using IMFMediaBuffer buffer = sample.ConvertToContiguousBuffer();
                buffer.Lock(out nint pointer, out _, out int length);
                try
                {
                    ReadOnlySpan<short> pcm = new((void*)pointer, length / 2);
                    for (int i = 0; i + 1 < pcm.Length; i += 2)
                    {
                        float left = pcm[i] / 32768f, right = pcm[i + 1] / 32768f;
                        sumSquares += left * left + right * right;
                        peak = Math.Max(peak, Math.Max(Math.Abs(left), Math.Abs(right)));
                        if (preview.Count < AudioTimeline.SampleRate * 10) preview.Add((left + right) / 2);
                        frames++;
                    }
                    duration = Math.Max(duration, timestamp + length / 4 * 10_000_000L / AudioTimeline.SampleRate);
                }
                finally { buffer.Unlock(); }
            }
            return new AudioProbe { HasAudio = true, SampleRate = AudioTimeline.SampleRate, Channels = 2, Frames = frames,
                Duration100Ns = duration, Rms = frames == 0 ? 0 : Math.Sqrt(sumSquares / (frames * 2d)), Peak = peak, PreviewMono = preview.ToArray() };
        }
    }
}
