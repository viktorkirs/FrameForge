using System.Runtime.InteropServices;
using FrameForge.Core;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;

namespace FrameForge.Encoding;

public sealed class EncoderDetails
{
    public required string Name { get; init; }
    public bool IsHardware { get; init; }
    public bool IsD3D11Aware { get; init; }
    public BindFlags BindFlags { get; init; }
    public FrameInputKind InputKind { get; init; }

    public string DisplayName => IsHardware ? Loc.Format("HardwareEncoder", Name) : Loc.Format("SoftwareEncoder", Name);
}

public enum FrameInputKind
{
    GpuNv12,
    GpuBgra,
    CpuBgra
}

public sealed class Mp4VideoWriter : IDisposable
{
    readonly IMFSinkWriter _writer;
    readonly IMFDXGIDeviceManager? _deviceManager;
    readonly int _stream;
    readonly int _audioStream;
    readonly object _callbackGate = new();
    readonly List<ReleaseCallback> _callbacks = new();
    static readonly List<ReleaseCallback> _retired = new();
    bool _started;
    bool _finished;
    bool _disposed;

    Mp4VideoWriter(IMFSinkWriter writer, IMFDXGIDeviceManager? deviceManager, int stream, EncoderDetails encoder, int audioStream = -1)
    {
        _writer = writer;
        _deviceManager = deviceManager;
        _stream = stream;
        _audioStream = audioStream;
        Encoder = encoder;
    }

    public EncoderDetails Encoder { get; }
    public FrameInputKind InputKind => Encoder.InputKind;

    public static Mp4VideoWriter Open(
        ID3D11Device device,
        string path,
        int width,
        int height,
        int fps,
        int bitrate,
        EncoderPreference preference,
        out string? warning,
        bool audio = false)
    {
        warning = null;
        if ((width & 1) != 0 || (height & 1) != 0)
            throw new ArgumentException(Loc.Get("EvenSize"));

        MediaFoundationStartup.Ensure();
        Exception? hardwareError = null;

        if (preference != EncoderPreference.Software)
        {
            try
            {
                return OpenHardware(device, path, width, height, fps, bitrate, audio);
            }
            catch (Exception ex)
            {
                hardwareError = ex;
                if (preference == EncoderPreference.Hardware)
                    throw new InvalidOperationException(Loc.Format("HardwareStartFailed", ex.Message), ex);
            }
        }

        try
        {
            Mp4VideoWriter software = OpenSoftware(path, width, height, fps, bitrate, audio);
            if (hardwareError != null)
                warning = Loc.Format("HardwareUnavailable", Trim(hardwareError.Message));
            return software;
        }
        catch (Exception ex)
        {
            string hardware = hardwareError == null ? "" : Loc.Format("HardwareTried", Trim(hardwareError.Message));
            throw new InvalidOperationException(Loc.Format("OpenFailed", hardware, Trim(ex.Message)), ex);
        }
    }

    public void WriteTexture(ID3D11Texture2D texture, long timestamp, long duration, bool discontinuity, Action released)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (InputKind == FrameInputKind.CpuBgra)
            throw new InvalidOperationException(Loc.Get("ExpectsMemory"));

        IMFMediaBuffer buffer = MediaFactory.MFCreateDXGISurfaceBuffer(typeof(ID3D11Texture2D).GUID, texture, 0, false);
        try
        {
            using IMF2DBuffer twoD = buffer.QueryInterface<IMF2DBuffer>();
            buffer.CurrentLength = twoD.ContiguousLength;
            WriteBuffer(buffer, timestamp, duration, discontinuity, released);
        }
        finally
        {
            buffer.Dispose();
        }
    }

    public unsafe void WriteBgra(ReadOnlySpan<byte> pixels, long timestamp, long duration, bool discontinuity)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (InputKind != FrameInputKind.CpuBgra)
            throw new InvalidOperationException(Loc.Get("ExpectsTexture"));

        IMFMediaBuffer buffer = MediaFactory.MFCreateMemoryBuffer(pixels.Length);
        try
        {
            buffer.Lock(out nint destination, out _, out _);
            try
            {
                pixels.CopyTo(new Span<byte>((void*)destination, pixels.Length));
            }
            finally
            {
                buffer.Unlock();
            }

            buffer.CurrentLength = pixels.Length;
            WriteBuffer(buffer, timestamp, duration, discontinuity, null);
        }
        finally
        {
            buffer.Dispose();
        }
    }

    public unsafe void WriteAudio(ReadOnlySpan<byte> pcm16Stereo, long firstFrame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_audioStream < 0 || pcm16Stereo.Length % 4 != 0)
            throw new InvalidOperationException("Stereo audio stream is not configured.");
        using IMFMediaBuffer buffer = MediaFactory.MFCreateMemoryBuffer(pcm16Stereo.Length);
        buffer.Lock(out nint destination, out _, out _);
        try { pcm16Stereo.CopyTo(new Span<byte>((void*)destination, pcm16Stereo.Length)); }
        finally { buffer.Unlock(); }
        buffer.CurrentLength = pcm16Stereo.Length;
        long time = firstFrame * 10_000_000L / AudioTimeline.SampleRate;
        long end = (firstFrame + pcm16Stereo.Length / 4) * 10_000_000L / AudioTimeline.SampleRate;
        WriteBuffer(buffer, time, end - time, firstFrame == 0, null, _audioStream);
    }

    public void Finish()
    {
        if (_disposed || !_started || _finished)
            return;

        _writer.NotifyEndOfSegment(_stream);
        if (_audioStream >= 0) _writer.NotifyEndOfSegment(_audioStream);
        _writer.Finalize();
        _finished = true;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        // Samples still in the encoder call back into ReleaseCallback. Dispose the writer
        // while those managed objects are rooted, or the COM shadow is collected and the process dies.
        _writer.Dispose();
        _deviceManager?.Dispose();
        lock (_callbackGate)
        {
            lock (_retired)
            {
                _retired.AddRange(_callbacks);
                if (_retired.Count > 180)
                    _retired.RemoveRange(0, _retired.Count - 180);
            }

            _callbacks.Clear();
        }
    }

    void Remember(ReleaseCallback callback)
    {
        lock (_callbackGate)
            _callbacks.Add(callback);
    }

    void Retire(ReleaseCallback callback)
    {
        // Media Foundation calls GetParameters on a thread of its own. The managed
        // callback has to stay rooted after WriteSample returns, or the COM shadow is collected.
        lock (_callbackGate)
            _callbacks.Remove(callback);
        lock (_retired)
        {
            _retired.Add(callback);
            if (_retired.Count > 180)
                _retired.RemoveRange(0, _retired.Count - 180);
        }
    }

    void WriteBuffer(IMFMediaBuffer buffer, long timestamp, long duration, bool discontinuity, Action? released, int? stream = null)
    {
        MediaFactory.MFCreateTrackedSample(out IMFTrackedSample tracked).CheckError();
        ReleaseCallback? callback = null;
        bool handedOff = false;
        try
        {
            using IMFSample sample = tracked.QueryInterface<IMFSample>();
            sample.AddBuffer(buffer);
            sample.SampleTime = timestamp;
            sample.SampleDuration = duration;
            if (discontinuity)
                sample.Set(SampleAttributeKeys.Discontinuity, 1u).CheckError();

            if (released != null)
            {
                callback = new ReleaseCallback(released, Retire);
                Remember(callback);
                tracked.SetAllocator(callback, null!);
            }

            _writer.WriteSample(stream ?? _stream, sample);
            handedOff = true;
        }
        finally
        {
            if (!handedOff)
                callback?.Complete();
            tracked.Dispose();
            GC.KeepAlive(callback);
        }
    }

    static Mp4VideoWriter OpenHardware(ID3D11Device device, string path, int width, int height, int fps, int bitrate, bool audio)
    {
        IMFDXGIDeviceManager manager = MediaFactory.MFCreateDXGIDeviceManager();
        try
        {
            manager.ResetDevice(device).CheckError();
            using IMFAttributes attributes = MediaFactory.MFCreateAttributes(6);
            attributes.Set(SinkWriterAttributeKeys.ReadwriteEnableHardwareTransforms, 1u).CheckError();
            attributes.Set(SinkWriterAttributeKeys.LowLatency, 1u).CheckError();
            attributes.Set(SinkWriterAttributeKeys.DisableThrottling, 1u).CheckError();
            attributes.Set(TranscodeAttributeKeys.TranscodeContainertype, TranscodeContainerTypeGuids.Mpeg4).CheckError();
            attributes.Set(SinkWriterAttributeKeys.D3DManager, manager).CheckError();

            IMFSinkWriter writer = MediaFactory.MFCreateSinkWriterFromURL(path, null!, attributes);
            try
            {
                int stream = ConfigureVideo(writer, VideoFormatGuids.Rgb32, width, height, fps, bitrate, hardware: true);
                EncoderDetails encoder = Inspect(writer, stream, FrameInputKind.GpuBgra);
                if (!encoder.IsD3D11Aware)
                    throw new InvalidOperationException(Loc.Format("EncoderNoD3D", encoder.Name));

                int audioStream = audio ? ConfigureAudio(writer) : -1;
                writer.BeginWriting();
                var opened = new Mp4VideoWriter(writer, manager, stream, encoder, audioStream) { _started = true };
                writer = null!;
                manager = null!;
                return opened;
            }
            finally
            {
                writer?.Dispose();
            }
        }
        finally
        {
            manager?.Dispose();
        }
    }

    static Mp4VideoWriter OpenSoftware(string path, int width, int height, int fps, int bitrate, bool audio)
    {
        using IMFAttributes attributes = MediaFactory.MFCreateAttributes(5);
        attributes.Set(SinkWriterAttributeKeys.ReadwriteEnableHardwareTransforms, 0u).CheckError();
        attributes.Set(SinkWriterAttributeKeys.LowLatency, 1u).CheckError();
        attributes.Set(SinkWriterAttributeKeys.DisableThrottling, 1u).CheckError();
        attributes.Set(TranscodeAttributeKeys.TranscodeContainertype, TranscodeContainerTypeGuids.Mpeg4).CheckError();

        IMFSinkWriter writer = MediaFactory.MFCreateSinkWriterFromURL(path, null!, attributes);
        try
        {
            int stream = ConfigureVideo(writer, VideoFormatGuids.Rgb32, width, height, fps, bitrate, hardware: false);
            EncoderDetails encoder = Inspect(writer, stream, FrameInputKind.CpuBgra);
            int audioStream = audio ? ConfigureAudio(writer) : -1;
            writer.BeginWriting();
            var opened = new Mp4VideoWriter(writer, null, stream, encoder, audioStream) { _started = true };
            writer = null!;
            return opened;
        }
        finally
        {
            writer?.Dispose();
        }
    }

    static int ConfigureAudio(IMFSinkWriter writer)
    {
        using IMFMediaType output = MediaFactory.MFCreateMediaType();
        output.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio).CheckError();
        output.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Aac).CheckError();
        output.Set(MediaTypeAttributeKeys.AudioNumChannels, 2u).CheckError();
        output.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)AudioTimeline.SampleRate).CheckError();
        output.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u).CheckError();
        output.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, 24000u).CheckError();
        output.Set(MediaTypeAttributeKeys.AacPayloadType, 0u).CheckError();
        int stream = writer.AddStream(output);
        using IMFMediaType input = MediaFactory.MFCreateMediaType();
        input.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio).CheckError();
        input.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Pcm).CheckError();
        input.Set(MediaTypeAttributeKeys.AudioNumChannels, 2u).CheckError();
        input.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)AudioTimeline.SampleRate).CheckError();
        input.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u).CheckError();
        input.Set(MediaTypeAttributeKeys.AudioBlockAlignment, 4u).CheckError();
        input.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, (uint)(AudioTimeline.SampleRate * 4)).CheckError();
        input.Set(MediaTypeAttributeKeys.AllSamplesIndependent, 1u).CheckError();
        writer.SetInputMediaType(stream, input, null!);
        return stream;
    }

    static int ConfigureVideo(IMFSinkWriter writer, Guid inputSubtype, int width, int height, int fps, int bitrate, bool hardware)
    {
        using IMFMediaType output = CreateVideoType(VideoFormatGuids.H264, width, height, fps);
        output.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)bitrate).CheckError();
        output.Set(MediaTypeAttributeKeys.Mpeg2Profile, hardware ? 100u : 77u).CheckError();
        int stream = writer.AddStream(output);

        using IMFMediaType input = CreateVideoType(inputSubtype, width, height, fps);
        if (inputSubtype == VideoFormatGuids.Rgb32)
        {
            input.Set(MediaTypeAttributeKeys.AllSamplesIndependent, 1u).CheckError();
            input.Set(MediaTypeAttributeKeys.DefaultStride, (uint)(width * 4)).CheckError();
        }

        writer.SetInputMediaType(stream, input, null!);
        return stream;
    }

    static IMFMediaType CreateVideoType(Guid subtype, int width, int height, int fps)
    {
        IMFMediaType type = MediaFactory.MFCreateMediaType();
        try
        {
            type.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video).CheckError();
            type.Set(MediaTypeAttributeKeys.Subtype, subtype).CheckError();
            type.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)VideoInterlaceMode.Progressive).CheckError();
            type.Set(MediaTypeAttributeKeys.FrameSize, Pack(width, height)).CheckError();
            type.Set(MediaTypeAttributeKeys.FrameRate, Pack(fps, 1)).CheckError();
            type.Set(MediaTypeAttributeKeys.PixelAspectRatio, Pack(1, 1)).CheckError();
            type.Set(MediaTypeAttributeKeys.VideoPrimaries, 2u).CheckError();
            type.Set(MediaTypeAttributeKeys.TransferFunction, 5u).CheckError();
            type.Set(MediaTypeAttributeKeys.YuvMatrix, 1u).CheckError();
            type.Set(MediaTypeAttributeKeys.VideoNominalRange, 2u).CheckError();
            IMFMediaType created = type;
            type = null!;
            return created;
        }
        finally
        {
            type?.Dispose();
        }
    }

    static EncoderDetails Inspect(IMFSinkWriter writer, int stream, FrameInputKind kind)
    {
        using IMFSinkWriterEx extended = writer.QueryInterface<IMFSinkWriterEx>();
        for (int index = 0; index < 8; index++)
        {
            Guid category;
            IMFTransform transform;
            try
            {
                extended.GetTransformForStream(stream, index, out category, out transform);
            }
            catch (SharpGenException)
            {
                break;
            }

            using (transform)
            {
                if (category != TransformCategoryGuids.VideoEncoder)
                    continue;

                using IMFAttributes attributes = transform.Attributes;
                string name = GetString(attributes, TransformAttributeKeys.MftFriendlyNameAttribute) ?? "H.264";
                bool hardware = !string.IsNullOrEmpty(GetString(attributes, TransformAttributeKeys.MftEnumHardwareUrlAttribute));
                bool d3d11 = attributes.GetUInt32(TransformAttributeKeys.D3D11Aware, out uint aware).Success && aware != 0;
                BindFlags bindFlags = BindFlags.RenderTarget;
                try
                {
                    using IMFAttributes input = transform.GetInputStreamAttributes(0);
                    if (input.GetUInt32(TransformAttributeKeys.D3D11Bindflags, out uint flags).Success)
                        bindFlags |= (BindFlags)flags;
                }
                catch (SharpGenException)
                {
                }

                return new EncoderDetails
                {
                    Name = name,
                    IsHardware = hardware,
                    IsD3D11Aware = d3d11,
                    BindFlags = bindFlags,
                    InputKind = kind
                };
            }
        }

        return new EncoderDetails
        {
            Name = kind == FrameInputKind.GpuNv12 ? "H.264" : Loc.Get("SoftwareH264"),
            IsHardware = kind == FrameInputKind.GpuNv12,
            IsD3D11Aware = kind == FrameInputKind.GpuNv12,
            BindFlags = BindFlags.RenderTarget,
            InputKind = kind
        };
    }

    static string? GetString(IMFAttributes attributes, Guid key)
    {
        try
        {
            return attributes.GetString(key);
        }
        catch (SharpGenException)
        {
            return null;
        }
    }

    static ulong Pack(int high, int low) => ((ulong)(uint)high << 32) | (uint)low;

    static string Trim(string message)
    {
        message = message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return message.Length > 220 ? message[..220] : message;
    }

    sealed class ReleaseCallback : CallbackBase, IMFAsyncCallback
    {
        readonly Action _released;
        readonly Action<ReleaseCallback> _retire;
        int _once;

        public ReleaseCallback(Action released, Action<ReleaseCallback> retire)
        {
            _released = released;
            _retire = retire;
        }

        public Result GetParameters(out AsyncCallbackFlags flags, out int queue)
        {
            flags = AsyncCallbackFlags.None;
            queue = 0;
            return Result.Ok;
        }

        public Result Invoke(IMFAsyncResult asyncResult)
        {
            Complete();
            return Result.Ok;
        }

        public void Complete()
        {
            if (Interlocked.Exchange(ref _once, 1) != 0)
                return;

            try
            {
                _released();
            }
            catch
            {
                // The texture slot still has to be returned.
            }

            _retire(this);
        }
    }
}

static class MediaFoundationStartup
{
    static readonly Lazy<bool> Startup = new(() =>
    {
        MediaFactory.MFStartup(false).CheckError();
        return true;
    });

    public static void Ensure() => _ = Startup.Value;
}

static class VideoFormatGuids
{
    public static Guid H264 { get; } = FromFourCc("H264");
    public static Guid Nv12 { get; } = FromFourCc("NV12");
    public static Guid Rgb32 => Vortice.MediaFoundation.VideoFormatGuids.Rgb32;

    static Guid FromFourCc(string fourCc) => Vortice.MediaFoundation.VideoFormatGuids.FromFourCC(new Vortice.FourCC(fourCc));
}
