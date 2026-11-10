using SharpGen.Runtime;
using System.Runtime.InteropServices;
using Vortice.MediaFoundation;

namespace FrameForge.Encoding;

public sealed class Mp4Probe
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required int SampleCount { get; init; }
    public required long Duration100Ns { get; init; }
    public required bool HasVideo { get; init; }
    public double LumaAverage { get; init; }
    public double LumaSpread { get; init; }
    public bool LooksBlank { get; init; }
    public byte[]? PreviewPixels { get; init; }
    public int PreviewWidth { get; init; }
    public int PreviewHeight { get; init; }

    public static Mp4Probe Read(string path) => Read(path, 8);

    public static Mp4Probe Read(string path, int previewSample, TimeSpan? seek = null, int maxSamples = int.MaxValue)
    {
        MediaFoundationStartup.Ensure();
        using IMFSourceReader reader = MediaFactory.MFCreateSourceReaderFromURL(path, null!);
        reader.SetStreamSelection((int)SourceReaderIndex.AllStreams, false);
        reader.SetStreamSelection((int)SourceReaderIndex.FirstVideoStream, true);
        using IMFMediaType native = reader.GetNativeMediaType((int)SourceReaderIndex.FirstVideoStream, 0);
        ulong packed = native.GetUInt64(MediaTypeAttributeKeys.FrameSize);
        int width = (int)(packed >> 32);
        int height = (int)(packed & 0xFFFFFFFF);

        using IMFMediaType output = MediaFactory.MFCreateMediaType();
        output.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video).CheckError();
        output.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Nv12).CheckError();
        reader.SetCurrentMediaType((int)SourceReaderIndex.FirstVideoStream, output);
        if (seek is TimeSpan position) reader.SetCurrentPosition(position.Ticks);

        int samples = 0;
        long lastTimestamp = 0;
        double lumaAverage = 0;
        double lumaSpread = 0;
        bool measured = false;
        byte[]? preview = null;
        int previewWidth = 0;
        int previewHeight = 0;
        int measureAt = Math.Max(1, previewSample);

        while (samples < maxSamples)
        {
            using IMFSample? sample = reader.ReadSample(
                (int)SourceReaderIndex.FirstVideoStream,
                (SourceReaderControlFlag)0,
                out _,
                out SourceReaderFlag flags,
                out long timestamp);

            if (flags.HasFlag(SourceReaderFlag.EndOfStream))
                break;
            if (sample == null)
                continue;

            samples++;
            lastTimestamp = timestamp;
            if (!measured && samples == measureAt)
            {
                using IMFMediaType decodedType = reader.GetCurrentMediaType((int)SourceReaderIndex.FirstVideoStream);
                ulong decodedSize = decodedType.GetUInt64(MediaTypeAttributeKeys.FrameSize);
                (lumaAverage, lumaSpread, preview, previewWidth, previewHeight) = Measure(sample, width, height,
                    (int)(decodedSize >> 32), (int)decodedSize);
                measured = true;
            }
        }

        if (!measured && samples > 0)
        {
            // The file was shorter than the preferred sample. Nothing else is decoded here.
        }

        return new Mp4Probe
        {
            Width = width,
            Height = height,
            SampleCount = samples,
            Duration100Ns = lastTimestamp,
            HasVideo = samples > 0,
            LumaAverage = lumaAverage,
            LumaSpread = lumaSpread,
            LooksBlank = measured && lumaSpread < 1.5,
            PreviewPixels = preview,
            PreviewWidth = previewWidth,
            PreviewHeight = previewHeight
        };
    }

    static (double Average, double Spread, byte[]? Preview, int PreviewWidth, int PreviewHeight) Measure(IMFSample sample, int width, int height, int storageWidth, int storageHeight)
    {
        using IMFMediaBuffer buffer = sample.ConvertToContiguousBuffer();
        using IMF2DBuffer? twoD = buffer.QueryInterfaceOrNull<IMF2DBuffer>();
        byte[] data;
        if (twoD != null)
        {
            // IMFMediaBuffer.Lock exposes decoder row padding. The 2D interface
            // removes that pitch instead of treating padded NV12 as tightly packed.
            data = new byte[twoD.ContiguousLength];
            twoD.ContiguousCopyTo(data, data.Length);
        }
        else
        {
            buffer.Lock(out nint pointer, out _, out int length);
            try { data = new byte[length]; Marshal.Copy(pointer, data, 0, length); }
            finally { buffer.Unlock(); }
        }
        {
            ReadOnlySpan<byte> pixels = data;
            int yPlane = storageWidth * storageHeight;
            if (pixels.Length < yPlane)
                return (0, 0, null, 0, 0);

            long sum = 0;
            long sumSquares = 0;
            int count = 0;
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x += 16)
                {
                    int luma = pixels[y * storageWidth + x];
                    sum += luma;
                    sumSquares += luma * luma;
                    count++;
                }

            double average = count == 0 ? 0 : sum / (double)count;
            double variance = count == 0 ? 0 : Math.Max(0, sumSquares / (double)count - average * average);
            int step = Math.Max(1, width / 480);
            int previewWidth = Math.Max(2, width / step);
            int previewHeight = Math.Max(2, height / step);
            var preview = new byte[previewWidth * previewHeight * 4];
            int uvOffset = yPlane;
            for (int y = 0; y < previewHeight; y++)
            {
                int sourceY = Math.Min(height - 1, y * step);
                for (int x = 0; x < previewWidth; x++)
                {
                    int sourceX = Math.Min(width - 1, x * step);
                    int yValue = pixels[sourceY * storageWidth + sourceX];
                    int uvIndex = uvOffset + (sourceY / 2) * storageWidth + (sourceX & ~1);
                    int u = uvIndex + 1 < pixels.Length ? pixels[uvIndex] : 128;
                    int v = uvIndex + 1 < pixels.Length ? pixels[uvIndex + 1] : 128;
                    float luma = (yValue - 16) / 219f;
                    float cb = (u - 128) / 224f;
                    float cr = (v - 128) / 224f;
                    int r = Clamp((luma + 1.5748f * cr) * 255f);
                    int g = Clamp((luma - 0.1873f * cb - 0.4681f * cr) * 255f);
                    int b = Clamp((luma + 1.8556f * cb) * 255f);
                    int destination = ((y * previewWidth) + x) * 4;
                    preview[destination] = (byte)b;
                    preview[destination + 1] = (byte)g;
                    preview[destination + 2] = (byte)r;
                    preview[destination + 3] = 255;
                }
            }

            return (average, Math.Sqrt(variance), preview, previewWidth, previewHeight);
        }
    }

    static int Clamp(float value) => (int)Math.Clamp(value, 0, 255);
}
