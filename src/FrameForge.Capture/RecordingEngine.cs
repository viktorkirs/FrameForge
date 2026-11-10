using System.Runtime.InteropServices;
using FrameForge.Core;
using FrameForge.Diagnostics;
using FrameForge.Encoding;
using Vortice.Direct3D11;

namespace FrameForge.Capture;

public sealed class RecordingEngine : IDisposable
{
    readonly object _stateGate = new();
    readonly MarkerState? _markers;

    public RecordingEngine(MarkerState? markers = null) => _markers = markers;
    Thread? _thread;
    volatile bool _stop;
    volatile bool _pause;
    GraphicsDevice? _device;
    WgcCapture? _capture;
    FrameOverlay? _overlay;
    PointerTrack? _pointer;
    int _cropX;
    int _cropY;
    int _cropWidth;
    int _cropHeight;
    bool _regionCapture;
    RecordingSnapshot _snapshot = RecordingSnapshot.Idle;

    public RecordingSnapshot Snapshot => _snapshot;
    public RecordingResult? LastResult { get; private set; }
    public bool IsActive => _thread != null;
    public event Action<BgraFrame>? PreviewReady;

    public void Start(MonitorInfo monitor, string outputPath, AppSettings settings) =>
        Start(new CaptureRequest { Monitor = monitor }, outputPath, settings);

    public void Start(CaptureRequest request, string outputPath, AppSettings settings)
    {
        if (_thread != null)
            throw new InvalidOperationException(Loc.Get("AlreadyRecording"));

        (int estimateWidth, int estimateHeight) = EstimateSize(request);
        (int width, int height) = OutputSize.Compute(estimateWidth, estimateHeight, settings.Resolution);
        int bitrate = settings.AutoBitrate
            ? VideoBitrate.Compute(settings.Quality, width, height, settings.Fps)
            : settings.ManualBitrateMbps * 1_000_000;

        _stop = false;
        _pause = false;
        _regionCapture = request.HasCrop && request.Window == 0;
        LastResult = null;
        Publish(new RecordingSnapshot
        {
            State = RecordingState.Starting,
            Width = width,
            Height = height,
            OutputPath = outputPath,
            Bitrate = bitrate,
            Message = Loc.Get("PreparingCapture")
        });

        Exception? startError = null;
        using var ready = new ManualResetEventSlim(false);
        var thread = new Thread(() => Run(request, outputPath, settings, width, height, bitrate, ready, error => startError = error))
        {
            IsBackground = true,
            Name = "FrameForge.Encode",
            Priority = ThreadPriority.AboveNormal
        };
        _thread = thread;
        thread.Start();
        ready.Wait();
        if (startError != null)
        {
            thread.Join();
            if (ReferenceEquals(_thread, thread))
                _thread = null;
            throw startError;
        }
    }

    static (int Width, int Height) EstimateSize(CaptureRequest request)
    {
        if (request.Window != 0 && WindowCatalog.TryGetBounds(request.Window, out _, out _, out int width, out int height))
            return (Math.Max(2, width & ~1), Math.Max(2, height & ~1));
        if (request.HasCrop)
            return (request.CropWidth, request.CropHeight);
        return (request.Monitor.Width, request.Monitor.Height);
    }

    public void Pause()
    {
        if (_thread != null && !_stop)
            _pause = true;
    }

    public void Resume() => _pause = false;

    public bool TryMoveRegion(int x, int y)
    {
        lock (_stateGate)
        {
            if (!_regionCapture || _capture == null || _stop ||
                _snapshot.State is not (RecordingState.Recording or RecordingState.Paused))
                return false;
            _cropX = Math.Clamp(x, 0, Math.Max(0, _capture.Width - _cropWidth)) & ~1;
            _cropY = Math.Clamp(y, 0, Math.Max(0, _capture.Height - _cropHeight)) & ~1;
            return true;
        }
    }

    public void Stop()
    {
        Thread? thread = _thread;
        if (thread == null)
            return;

        _stop = true;
        _pause = false;
        Publish(Copy(_snapshot, RecordingState.Stopping, Loc.Get("FinishingFile")));
        thread.Join();
        _thread = null;
    }

    public BgraFrame? CopyCurrentFrame(int maxEdge = 0)
    {
        GraphicsDevice? device = _device;
        WgcCapture? capture = _capture;
        if (device == null || capture == null || capture.Generation == 0)
            return null;

        lock (_stateGate)
        {
            if (_device == null || _capture == null)
                return null;
            ID3D11Texture2D source = _overlay is { Ready: true } ? _overlay.Texture : _capture.Latest;
            if (IsPartialCrop)
                return FrameGrabber.ReadBox(_device, source, _cropX, _cropY, _cropWidth, _cropHeight, maxEdge);
            return FrameGrabber.ReadTexture(_device, source, _capture.Width, _capture.Height, maxEdge);
        }
    }

    public void Dispose() => Stop();

    bool IsPartialCrop =>
        _capture != null && _cropWidth >= 16 && _cropHeight >= 16
        && (_cropX != 0 || _cropY != 0 || _cropWidth != _capture.Width || _cropHeight != _capture.Height);

    void Run(
        CaptureRequest request,
        string outputPath,
        AppSettings settings,
        int width,
        int height,
        int bitrate,
        ManualResetEventSlim ready,
        Action<Exception> reportStartError)
    {
        Mp4VideoWriter? writer = null;
        AudioCapture? audio = null;
        GpuScaler? converter = null;
        ID3D11Texture2D? bgraStaging = null;
        var recent = new Queue<long>();
        long written = 0;
        long repeated = 0;
        long dropped = 0;
        long frameIndex = 0;
        long seenGeneration = -1;
        long clockStart = 0;
        bool clockStarted = false;
        string? warning = null;
        string? error = null;
        int fps = settings.Fps;
        long nextPreview = 0;

        try
        {
            bool smoothCursor = settings.SmoothCursor && settings.CaptureCursor;
            bool highlightClicks = settings.HighlightClicks;
            MonitorInfo monitor = request.Monitor;
            _device = GraphicsDevice.CreateForMonitor(new MonitorHandle(monitor.Handle));
            bool captureCursor = settings.CaptureCursor && !smoothCursor;
            if (request.Window != 0)
            {
                try
                {
                    _capture = WgcCapture.StartWindow(_device, request.Window, captureCursor, fps);
                }
                catch (Exception ex) when (ex is not InvalidOperationException and not TimeoutException)
                {
                    Log.Error("Захват окна не стартовал.", ex);
                    throw new InvalidOperationException(Loc.Get("NoFrameWindow"), ex);
                }
            }
            else
            {
                _capture = WgcCapture.Start(_device, monitor, captureCursor, fps);
            }

            if (!_capture.WaitForFrame(TimeSpan.FromSeconds(3)))
                throw new TimeoutException(request.Window != 0 ? Loc.Get("NoFrameWindow") : Loc.Get("NoFrame"));

            ResolveCrop(request);
            (width, height) = OutputSize.Compute(_cropWidth, _cropHeight, settings.Resolution);
            if (settings.AutoBitrate)
                bitrate = VideoBitrate.Compute(settings.Quality, width, height, fps);
            Log.Info($"Capture source {(_cropWidth == _capture.Width && _cropHeight == _capture.Height ? "full" : $"crop {_cropWidth}x{_cropHeight} at {_cropX},{_cropY}")} -> {width}x{height}");

            if (highlightClicks || smoothCursor || settings.TemporaryMarker)
            {
                _overlay = new FrameOverlay(_device, _capture.Width, _capture.Height, settings.TemporaryMarker);
                _pointer = new PointerTrack(monitor, _capture.Width, _capture.Height, smoothCursor, highlightClicks, request.Window,
                    settings.CursorMotion, settings.CursorSmoothnessMilliseconds);
                _pointer.Sample(Qpc.Now());
                Log.Info($"Recording overlay clicks={highlightClicks} smoothCursor={smoothCursor}");
            }

            if (settings.SystemAudio || settings.Microphone)
                audio = new AudioCapture(settings);
            writer = Mp4VideoWriter.Open(_device.Device, outputPath, width, height, fps, bitrate, settings.Encoder, out warning, audio != null);
            Log.Info($"Encoder {writer.Encoder.Name} hardware={writer.Encoder.IsHardware} {width}x{height}@{fps} {bitrate} bps");
            int readWidth = IsPartialCrop ? _cropWidth : _capture.Width;
            int readHeight = IsPartialCrop ? _cropHeight : _capture.Height;
            if (writer.InputKind != FrameInputKind.CpuBgra)
            {
                try
                {
                    converter = new GpuScaler(
                        _device,
                        _capture.Width,
                        _capture.Height,
                        width,
                        height,
                        writer.Encoder.BindFlags,
                        _cropX,
                        _cropY,
                        _cropWidth,
                        _cropHeight);
                }
                catch (Exception convertError) when (settings.Encoder != EncoderPreference.Hardware)
                {
                    Log.Error("GPU-конвертация недоступна, переход на программный кодировщик.", convertError);
                    writer.Dispose();
                    try { File.Delete(outputPath); } catch { /* A new writer overwrites only when the path is free. */ }
                    writer = Mp4VideoWriter.Open(_device.Device, outputPath, width, height, fps, bitrate, EncoderPreference.Software, out _, audio != null);
                    warning = Loc.Get("SoftwareFallback");
                    bgraStaging = CreateStaging(_device, readWidth, readHeight);
                }
            }
            else
            {
                bgraStaging = CreateStaging(_device, readWidth, readHeight);
            }

            audio?.Start();
            clockStart = Qpc.Now();
            clockStarted = true;
            audio?.Begin((long)(clockStart * (10_000_000d / Qpc.Frequency)));

            Publish(new RecordingSnapshot
            {
                State = RecordingState.Recording,
                EncoderName = writer.Encoder.DisplayName,
                HardwareEncoder = writer.Encoder.IsHardware,
                Width = width,
                Height = height,
                OutputPath = outputPath,
                Bitrate = bitrate,
                Message = warning,
                FreeDiskBytes = FreeDisk(outputPath)
            });
            ready.Set();

            while (!_stop)
            {
                audio?.ThrowIfFailed();
                if (_capture.IsClosed)
                    throw new InvalidOperationException(request.Window != 0 ? Loc.Get("WindowClosed") : Loc.Get("NoFrame"));
                if (request.Window != 0)
                {
                    if (!WindowCatalog.IsAlive(request.Window))
                        throw new InvalidOperationException(Loc.Get("WindowClosed"));
                    if (WindowCatalog.IsMinimized(request.Window))
                        throw new InvalidOperationException(Loc.Get("WindowMinimized"));
                }

                if (_pause)
                {
                    long pausedAt = Qpc.Now();
                    audio?.Pause((long)(pausedAt * (10_000_000d / Qpc.Frequency)));
                    Publish(Copy(_snapshot, RecordingState.Paused, _snapshot.Message));
                    while (_pause && !_stop)
                    {
                        audio?.ThrowIfFailed();
                        Thread.Sleep(10);
                    }
                    if (clockStarted)
                        clockStart += Qpc.Now() - pausedAt;
                    _pointer?.FreezeClock(Qpc.Now());
                    audio?.Resume(AudioCapture.WallNow());
                    if (!_stop)
                        Publish(Copy(_snapshot, RecordingState.Recording, _snapshot.Message));
                    continue;
                }

                long now = Qpc.Now();
                _pointer?.Sample(now);
                if (clockStarted)
                {
                    long due = clockStart + (frameIndex * Qpc.Frequency / fps);
                    if (now + (Qpc.Frequency / 500) < due)
                    {
                        Thread.Sleep(1);
                        continue;
                    }

                    long ideal = (now - clockStart) * fps / Qpc.Frequency;
                    if (ideal > frameIndex + 3)
                    {
                        dropped += ideal - frameIndex;
                        frameIndex = ideal;
                    }
                }

                bool isRepeat = seenGeneration == _capture.Generation;
                if (isRepeat)
                    repeated++;
                seenGeneration = _capture.Generation;

                bool wrote = WriteFrame(writer, converter, bgraStaging, frameIndex, fps, discontinuity: frameIndex == 0);
                if (!wrote)
                    dropped++;
                else
                    written++;

                if (!clockStarted)
                {
                    clockStart = Qpc.Now();
                    clockStarted = true;
                }

                frameIndex++;
                audio?.Pump(writer, frameIndex * AudioTimeline.SampleRate / fps);
                now = Qpc.Now();
                recent.Enqueue(now);
                while (recent.Count > 0 && now - recent.Peek() > Qpc.Frequency)
                    recent.Dequeue();

                if (now >= nextPreview)
                {
                    nextPreview = now + (Qpc.Frequency / 4);
                    PublishPreview();
                    long free = FreeDisk(outputPath);
                    if (free is >= 0 and < RecordingSafety.DiskStopBytes)
                        throw new IOException(Loc.Get("LowDisk"));

                    Publish(new RecordingSnapshot
                    {
                        State = _pause ? RecordingState.Paused : RecordingState.Recording,
                        Elapsed = TimeSpan.FromTicks(frameIndex * TimeSpan.TicksPerSecond / fps),
                        FileBytes = FileLength(outputPath),
                        FreeDiskBytes = free,
                        ActualFps = recent.Count,
                        CapturedFrames = _capture.Generation,
                        WrittenFrames = written,
                        RepeatedFrames = repeated,
                        DroppedFrames = dropped,
                        EncoderName = writer.Encoder.DisplayName,
                        HardwareEncoder = writer.Encoder.IsHardware,
                        Message = warning,
                        Width = width,
                        Height = height,
                        OutputPath = outputPath,
                        Bitrate = bitrate,
                        SystemAudioLevel = audio?.ReadSystemLevel() ?? 0,
                        MicrophoneLevel = audio?.ReadMicrophoneLevel() ?? 0
                    });
                }
            }
        }
        catch (Exception ex)
        {
            error = ex is OutOfMemoryException ? Loc.Get("OutOfMemory") : ex.Message;
            Log.Error(ex is OutOfMemoryException ? "Не хватило памяти." : "Сбой записи.", ex);
            if (!ready.IsSet)
            {
                reportStartError(ex);
                ready.Set();
            }
            else
            {
                Publish(Copy(_snapshot, RecordingState.Failed, error));
            }
        }
        finally
        {
            if (writer != null && ready.IsSet)
                Publish(Copy(_snapshot, RecordingState.Stopping, Loc.Get("FinishingFile")));

            try
            {
                audio?.Stop();
                if (audio != null && writer != null && frameIndex > 0)
                    audio.Pump(writer, frameIndex * AudioTimeline.SampleRate / fps, flush: true);
            }
            catch (Exception ex)
            {
                error ??= Loc.Format("AudioCaptureFailed", Loc.Get("AudioTab"), ex.Message);
                Log.Error("Finalize audio.", ex);
            }
            try
            {
                writer?.Finish();
            }
            catch (Exception ex)
            {
                error ??= Loc.Format("FinishFailed", ex.Message);
                Log.Error("Finalize MP4.", ex);
            }

            try { audio?.Dispose(); } catch (Exception ex) { Log.Error("Audio cleanup.", ex); }
            writer?.Dispose();
            converter?.Dispose();
            bgraStaging?.Dispose();
            FrameOverlay? overlay;
            lock (_stateGate)
            {
                overlay = _overlay;
                _overlay = null;
                _pointer = null;
            }

            try { overlay?.Dispose(); } catch (Exception ex) { Log.Error("Не удалось закрыть оверлей.", ex); }
            try { _capture?.Dispose(); } catch (Exception ex) { Log.Error("Не удалось закрыть захват.", ex); }
            _capture = null;
            _cropX = _cropY = _cropWidth = _cropHeight = 0;
            try { _device?.Dispose(); } catch (Exception ex) { Log.Error("Не удалось закрыть Direct3D.", ex); }
            _device = null;

            LastResult = new RecordingResult
            {
                AudioFrames = audio?.WrittenFrames ?? 0,
                SystemAudioPackets = audio?.SystemPackets ?? 0,
                MicrophonePackets = audio?.MicrophonePackets ?? 0,
                Path = outputPath,
                EncoderName = _snapshot.EncoderName,
                HardwareEncoder = _snapshot.HardwareEncoder,
                WrittenFrames = written,
                RepeatedFrames = repeated,
                DroppedFrames = dropped,
                CapturedFrames = _snapshot.CapturedFrames,
                Width = width,
                Height = height,
                Fps = fps,
                Warning = warning,
                Error = error
            };

            Publish(new RecordingSnapshot
            {
                State = error == null ? RecordingState.Idle : RecordingState.Failed,
                Elapsed = TimeSpan.FromTicks(frameIndex * TimeSpan.TicksPerSecond / fps),
                FileBytes = FileLength(outputPath),
                FreeDiskBytes = FreeDisk(outputPath),
                WrittenFrames = written,
                RepeatedFrames = repeated,
                DroppedFrames = dropped,
                CapturedFrames = LastResult.CapturedFrames,
                EncoderName = LastResult.EncoderName,
                HardwareEncoder = LastResult.HardwareEncoder,
                Width = width,
                Height = height,
                OutputPath = outputPath,
                Bitrate = bitrate,
                Message = error ?? warning
            });

            if (!ready.IsSet)
                ready.Set();

            if (ReferenceEquals(_thread, Thread.CurrentThread))
                _thread = null;

            Log.Info($"Recording finished frames={written} repeated={repeated} dropped={dropped} error={error}");
        }
    }

    void ResolveCrop(CaptureRequest request)
    {
        if (_capture == null)
            return;

        int cropX = 0;
        int cropY = 0;
        int cropWidth = _capture.Width;
        int cropHeight = _capture.Height;
        if (request.HasCrop)
        {
            cropX = Math.Clamp(request.CropX, 0, Math.Max(0, _capture.Width - 2)) & ~1;
            cropY = Math.Clamp(request.CropY, 0, Math.Max(0, _capture.Height - 2)) & ~1;
            cropWidth = Math.Min(request.CropWidth & ~1, _capture.Width - cropX);
            cropHeight = Math.Min(request.CropHeight & ~1, _capture.Height - cropY);
            cropWidth = Math.Max(2, cropWidth & ~1);
            cropHeight = Math.Max(2, cropHeight & ~1);
        }

        _cropX = cropX;
        _cropY = cropY;
        _cropWidth = cropWidth;
        _cropHeight = cropHeight;
    }

    bool WriteFrame(Mp4VideoWriter writer, GpuScaler? converter, ID3D11Texture2D? staging, long frameIndex, int fps, bool discontinuity)
    {
        long timestamp = frameIndex * 10_000_000L / fps;
        long duration = ((frameIndex + 1) * 10_000_000L / fps) - timestamp;
        if (writer.InputKind != FrameInputKind.CpuBgra)
        {
            if (converter == null || _capture == null || _device == null)
                return false;

            lock (_stateGate)
            {
                ID3D11Texture2D source = PrepareFrame();
                converter.UpdateCrop(_cropX, _cropY, _cropWidth, _cropHeight);
                _device.Context.CopyResource(converter.Source, source);
            }

            if (!converter.TryConvert(out ID3D11Texture2D? texture, out Action? release) || texture == null || release == null)
                return false;

            try
            {
                writer.WriteTexture(texture, timestamp, duration, discontinuity, release);
                return true;
            }
            catch
            {
                release();
                throw;
            }
        }

        if (staging == null || _capture == null || _device == null)
            return false;

        BgraFrame frame;
        lock (_stateGate)
        {
            ID3D11Texture2D source = PrepareFrame();
            frame = IsPartialCrop
                ? FrameGrabber.ReadBox(_device, source, _cropX, _cropY, _cropWidth, _cropHeight, 0)
                : FrameGrabber.ReadTexture(_device, source, _capture.Width, _capture.Height, 0);
        }

        if (frame.Width != _snapshot.Width || frame.Height != _snapshot.Height)
            frame = ScaleFrame(frame, _snapshot.Width, _snapshot.Height);

        writer.WriteBgra(frame.Pixels, timestamp, duration, discontinuity);
        return true;
    }

    ID3D11Texture2D PrepareFrame()
    {
        if (_capture == null)
            throw new InvalidOperationException(Loc.Get("NoFrame"));

        if (_overlay == null || _pointer == null)
            return _capture.Latest;

        _pointer.Sample(Qpc.Now());
        _overlay.Compose(_capture.Latest, _pointer, _markers?.Snapshot());
        return _overlay.Texture;
    }

    void PublishPreview()
    {
        try
        {
            BgraFrame? frame = CopyCurrentFrame(960);
            if (frame != null)
                PreviewReady?.Invoke(frame);
        }
        catch (Exception ex)
        {
            Log.Error("Превью кадра не собралось.", ex);
        }
    }

    static BgraFrame ScaleFrame(BgraFrame source, int width, int height)
    {
        if (width <= 0 || height <= 0)
            return source;
        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            int sourceY = Math.Min(source.Height - 1, y * source.Height / height);
            for (int x = 0; x < width; x++)
            {
                int sourceX = Math.Min(source.Width - 1, x * source.Width / width);
                int from = ((sourceY * source.Width) + sourceX) * 4;
                int to = ((y * width) + x) * 4;
                pixels[to] = source.Pixels[from];
                pixels[to + 1] = source.Pixels[from + 1];
                pixels[to + 2] = source.Pixels[from + 2];
                pixels[to + 3] = source.Pixels[from + 3];
            }
        }

        return new BgraFrame { Width = width, Height = height, Pixels = pixels };
    }

    static ID3D11Texture2D CreateStaging(GraphicsDevice device, int width, int height) =>
        device.Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Vortice.DXGI.Format.B8G8R8A8_UNorm,
            SampleDescription = new Vortice.DXGI.SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None
        });

    static RecordingSnapshot Copy(RecordingSnapshot source, RecordingState state, string? message) => new()
    {
        State = state,
        Elapsed = source.Elapsed,
        FileBytes = source.FileBytes,
        FreeDiskBytes = source.FreeDiskBytes,
        ActualFps = source.ActualFps,
        CapturedFrames = source.CapturedFrames,
        WrittenFrames = source.WrittenFrames,
        RepeatedFrames = source.RepeatedFrames,
        DroppedFrames = source.DroppedFrames,
        EncoderName = source.EncoderName,
        HardwareEncoder = source.HardwareEncoder,
        Message = message,
        Width = source.Width,
        Height = source.Height,
        OutputPath = source.OutputPath,
        Bitrate = source.Bitrate,
        SystemAudioLevel = state == RecordingState.Recording ? source.SystemAudioLevel : 0,
        MicrophoneLevel = state == RecordingState.Recording ? source.MicrophoneLevel : 0
    };

    void Publish(RecordingSnapshot snapshot) => _snapshot = snapshot;

    static long FileLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch
        {
            return 0;
        }
    }

    static long FreeDisk(string path)
    {
        try
        {
            string root = Path.GetPathRoot(Path.GetFullPath(path)) ?? "";
            return string.IsNullOrEmpty(root) ? -1 : new DriveInfo(root).AvailableFreeSpace;
        }
        catch
        {
            return -1;
        }
    }
}

static class Qpc
{
    public static readonly long Frequency;

    static Qpc()
    {
        QueryPerformanceFrequency(out Frequency);
    }

    public static long Now()
    {
        QueryPerformanceCounter(out long value);
        return value;
    }

    [DllImport("kernel32.dll")]
    static extern bool QueryPerformanceCounter(out long value);

    [DllImport("kernel32.dll")]
    static extern bool QueryPerformanceFrequency(out long value);
}
