using System.Runtime.InteropServices;
using FrameForge.Core;
using FrameForge.Diagnostics;
using FrameForge.Encoding;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace FrameForge.Capture;

sealed class AudioCapture : IDisposable
{
    readonly AudioTimeline _timeline;
    readonly AudioLevelTracker _systemLevel = new();
    readonly AudioLevelTracker _microphoneLevel = new();
    readonly List<(WasapiRecorder Recorder, MMDevice Device)> _sources = [];
    readonly short[] _pcm = new short[960 * AudioTimeline.Channels];
    Exception? _failure;
    volatile bool _stopping;
    bool _stopped;
    long _systemPackets, _microphonePackets;

    public long WrittenFrames { get; private set; }
    public long SystemPackets => Interlocked.Read(ref _systemPackets);
    public long MicrophonePackets => Interlocked.Read(ref _microphonePackets);
    public double ReadSystemLevel() => _systemLevel.ReadLevel();
    public double ReadMicrophoneLevel() => _microphoneLevel.ReadLevel();

    public AudioCapture(AppSettings settings)
    {
        _timeline = new AudioTimeline((settings.SystemAudio ? 1 : 0) + (settings.Microphone ? 1 : 0));
        try
        {
            if (settings.SystemAudio) AddSource(settings.SystemAudioDeviceId, system: true);
            if (settings.Microphone) AddSource(settings.MicrophoneDeviceId, system: false);
        }
        catch { Dispose(); throw; }
    }

    void AddSource(string? id, bool system)
    {
        MMDevice? device = null;
        WasapiRecorder? recorder = null;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            device = string.IsNullOrWhiteSpace(id)
                ? enumerator.GetDefaultAudioEndpoint(system ? DataFlow.Render : DataFlow.Capture, Role.Multimedia)
                : enumerator.GetDevice(id);
            var builder = new WasapiRecorderBuilder().WithDevice(device)
                .WithFormat(WaveFormat.CreateIeeeFloatWaveFormat(AudioTimeline.SampleRate, AudioTimeline.Channels))
                .WithBufferLength(20);
            if (system) builder.WithLoopbackCapture().WithPollingSync();
            recorder = builder.Build();
            int source = _sources.Count;
            string label = Loc.Get(system ? "SystemAudio" : "Microphone");
            recorder.DataAvailable += (data, flags, _, qpc) =>
            {
                try
                {
                    if (system) Interlocked.Increment(ref _systemPackets); else Interlocked.Increment(ref _microphonePackets);
                    long stamp = qpc > 0 && (flags & AudioClientBufferFlags.TimestampError) == 0
                        ? qpc : WallNow() - data.Length / 8 * 10_000_000L / AudioTimeline.SampleRate;
                    ReadOnlySpan<float> samples = MemoryMarshal.Cast<byte, float>(data);
                    (system ? _systemLevel : _microphoneLevel).Report(samples);
                    _timeline.AddPacket(source, samples, stamp);
                }
                catch (Exception ex) { Interlocked.CompareExchange(ref _failure, new InvalidOperationException(Loc.Format("AudioCaptureFailed", label, ex.Message), ex), null); }
            };
            recorder.RecordingStopped += (_, args) =>
            {
                if (!_stopping && args.Exception != null)
                    Interlocked.CompareExchange(ref _failure, new InvalidOperationException(Loc.Format("AudioCaptureFailed", label, args.Exception.Message), args.Exception), null);
            };
            _sources.Add((recorder, device));
            Log.Info($"Audio source {label}: {device.FriendlyName} -> {recorder.WaveFormat}");
        }
        catch (Exception ex)
        {
            recorder?.Dispose();
            device?.Dispose();
            throw new InvalidOperationException(Loc.Format("AudioCaptureFailed", Loc.Get(system ? "SystemAudio" : "Microphone"), ex.Message), ex);
        }
    }

    public void Start()
    {
        for (int i = 0; i < _sources.Count; i++)
        {
            try { _sources[i].Recorder.StartRecording(); }
            catch (Exception ex)
            {
                throw new InvalidOperationException(Loc.Format("AudioCaptureFailed", _sources[i].Device.FriendlyName, ex.Message), ex);
            }
        }
    }

    public void Begin(long qpc100Ns) { ResetLevels(); _timeline.Begin(qpc100Ns); }
    public void Pause(long qpc100Ns) { ResetLevels(); _timeline.Pause(qpc100Ns); }
    public void Resume(long qpc100Ns) { ResetLevels(); _timeline.Resume(qpc100Ns); }
    void ResetLevels() { _systemLevel.Reset(); _microphoneLevel.Reset(); }
    public void ThrowIfFailed() { if (Volatile.Read(ref _failure) is Exception error) throw error; }

    public void Pump(Mp4VideoWriter writer, long videoAudioFrames, bool flush = false)
    {
        long until = flush ? videoAudioFrames : Math.Max(0, videoAudioFrames - AudioTimeline.SampleRate / 10);
        while (WrittenFrames < until)
        {
            int frames = (int)Math.Min(_pcm.Length / AudioTimeline.Channels, until - WrittenFrames);
            Span<short> pcm = _pcm.AsSpan(0, frames * AudioTimeline.Channels);
            _timeline.ReadPcm(pcm);
            writer.WriteAudio(MemoryMarshal.AsBytes(pcm), WrittenFrames);
            WrittenFrames += frames;
        }
    }

    public void Stop()
    {
        if (_stopped) return;
        _stopped = true;
        _stopping = true;
        foreach (var source in _sources) source.Recorder.StopRecording();
        foreach (var source in _sources)
            try { source.Recorder.Dispose(); }
            catch (Exception ex) { Log.Error("Audio capture cleanup.", ex); }
    }

    public void Dispose()
    {
        Stop();
        foreach (var source in _sources)
            try { source.Device.Dispose(); }
            catch (Exception ex) { Log.Error("Audio device cleanup.", ex); }
        _sources.Clear();
    }

    public static long WallNow() => (long)(Qpc.Now() * (10_000_000d / Qpc.Frequency));
}
