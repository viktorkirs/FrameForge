using System.Runtime.InteropServices;
using FrameForge.Core;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace FrameForge.Capture;

/// <summary>Records only the chosen microphone, aligned to the editor's playback epoch.</summary>
public sealed class VoiceoverRecorder : IDisposable
{
    readonly AudioTimeline _timeline = new(1);
    readonly AudioLevelTracker _level = new();
    readonly short[] _pcm = new short[960 * AudioTimeline.Channels];
    readonly MMDevice _device;
    readonly WasapiRecorder _recorder;
    readonly WaveFileWriter _writer;
    Exception? _failure;
    long _frames;
    volatile bool _stopping;
    bool _disposed;

    public string FilePath { get; }
    public double Level => _level.ReadLevel();
    public TimeSpan Duration => TimeSpan.FromSeconds(_frames / (double)AudioTimeline.SampleRate);

    public VoiceoverRecorder(string path, string? deviceId)
    {
        FilePath = path;
        using var enumerator = new MMDeviceEnumerator();
        _device = string.IsNullOrWhiteSpace(deviceId)
            ? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia) : enumerator.GetDevice(deviceId);
        try
        {
            _recorder = new WasapiRecorderBuilder().WithDevice(_device)
                .WithFormat(WaveFormat.CreateIeeeFloatWaveFormat(AudioTimeline.SampleRate, AudioTimeline.Channels))
                .WithBufferLength(20).Build();
            try { _writer = new WaveFileWriter(path, new WaveFormat(AudioTimeline.SampleRate, 16, AudioTimeline.Channels)); }
            catch { _recorder.Dispose(); throw; }
        }
        catch { _device.Dispose(); throw; }
        _recorder.DataAvailable += (data, flags, _, qpc) =>
        {
            try
            {
                ReadOnlySpan<float> samples = MemoryMarshal.Cast<byte, float>(data);
                _level.Report(samples);
                long stamp = qpc > 0 && (flags & AudioClientBufferFlags.TimestampError) == 0
                    ? qpc : AudioCapture.WallNow() - samples.Length / 2 * TimeSpan.TicksPerSecond / AudioTimeline.SampleRate;
                _timeline.AddPacket(0, samples, stamp);
            }
            catch (Exception ex) { Interlocked.CompareExchange(ref _failure, ex, null); }
        };
        _recorder.RecordingStopped += (_, args) =>
        {
            if (!_stopping && args.Exception != null) Interlocked.CompareExchange(ref _failure, args.Exception, null);
        };
    }

    public void Start() => _recorder.StartRecording();
    public void Begin() => _timeline.Begin(AudioCapture.WallNow());

    public void Pump(TimeSpan duration, bool flush = false)
    {
        if (Volatile.Read(ref _failure) is { } error) throw new InvalidOperationException(Loc.Format("EditorVoiceFailed", error.Message), error);
        long until = Math.Max(0, (long)Math.Round(duration.TotalSeconds * AudioTimeline.SampleRate) - (flush ? 0 : AudioTimeline.SampleRate / 10));
        while (_frames < until)
        {
            int frames = (int)Math.Min(_pcm.Length / AudioTimeline.Channels, until - _frames);
            Span<short> pcm = _pcm.AsSpan(0, frames * AudioTimeline.Channels);
            _timeline.ReadPcm(pcm);
            _writer.Write(MemoryMarshal.AsBytes(pcm));
            _frames += frames;
        }
    }

    public TimeSpan Stop(TimeSpan duration)
    {
        _stopping = true;
        _recorder.StopRecording();
        _recorder.Dispose();
        Pump(duration, flush: true);
        _writer.Flush();
        return Duration;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stopping = true;
        try { _recorder.Dispose(); }
        finally { try { _writer.Dispose(); } finally { _device.Dispose(); } }
    }
}
