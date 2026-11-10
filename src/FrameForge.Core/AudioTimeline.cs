namespace FrameForge.Core;

/// <summary>Aligns independently clocked capture packets to the video clock and mixes stereo PCM.</summary>
public sealed class AudioTimeline
{
    public const int SampleRate = 48000;
    public const int Channels = 2;
    readonly object _gate = new();
    readonly int _capacity;
    readonly float[][] _samples;
    readonly long[][] _tags;
    readonly List<(long Start, long End)> _pauses = [];
    long _epoch;
    long? _pauseStart;
    long _readFrame;
    bool _started;

    public AudioTimeline(int sources, int bufferSeconds = 4)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sources, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(sources, 2);
        _capacity = SampleRate * Math.Clamp(bufferSeconds, 1, 10);
        _samples = Enumerable.Range(0, sources).Select(_ => new float[_capacity * Channels]).ToArray();
        _tags = Enumerable.Range(0, sources).Select(_ => Enumerable.Repeat(-1L, _capacity).ToArray()).ToArray();
    }

    public void Begin(long qpc100Ns)
    {
        lock (_gate) { _epoch = qpc100Ns; _started = true; }
    }

    public void Pause(long qpc100Ns)
    {
        lock (_gate) _pauseStart ??= qpc100Ns;
    }

    public void Resume(long qpc100Ns)
    {
        lock (_gate)
        {
            if (_pauseStart is long start)
            {
                _pauses.Add((start, Math.Max(start, qpc100Ns)));
                _pauseStart = null;
            }
        }
    }

    public void AddPacket(int source, ReadOnlySpan<float> stereo, long qpc100Ns)
    {
        if ((uint)source >= (uint)_samples.Length || stereo.Length % Channels != 0)
            throw new ArgumentException("Invalid audio packet.");
        lock (_gate)
        {
            if (!_started) return;
            for (int i = 0; i < stereo.Length / Channels; i++)
            {
                long wall = qpc100Ns + i * 10_000_000L / SampleRate;
                if (wall < _epoch) continue;
                long removed = 0;
                bool paused = _pauseStart is long active && wall >= active;
                foreach ((long start, long end) in _pauses)
                {
                    if (wall >= end) removed += end - start;
                    else if (wall >= start) { paused = true; break; }
                    else break;
                }
                if (paused) continue;
                long frame = (long)Math.Round((wall - _epoch - removed) * (SampleRate / 10_000_000d));
                if (frame < _readFrame) continue;
                if (frame - _readFrame >= _capacity)
                    throw new InvalidOperationException(Loc.Get("AudioBufferOverflow"));
                int slot = (int)(frame % _capacity);
                _tags[source][slot] = frame;
                float left = stereo[i * Channels], right = stereo[i * Channels + 1];
                _samples[source][slot * Channels] = float.IsFinite(left) ? left : 0;
                _samples[source][slot * Channels + 1] = float.IsFinite(right) ? right : 0;
            }
        }
    }

    public void ReadPcm(Span<short> stereo)
    {
        if (stereo.Length % Channels != 0) throw new ArgumentException("Stereo PCM required.");
        lock (_gate)
        {
            for (int i = 0; i < stereo.Length / Channels; i++, _readFrame++)
            {
                int slot = (int)(_readFrame % _capacity);
                float left = 0, right = 0;
                for (int source = 0; source < _samples.Length; source++)
                    if (_tags[source][slot] == _readFrame)
                    {
                        left += _samples[source][slot * Channels];
                        right += _samples[source][slot * Channels + 1];
                    }
                stereo[i * Channels] = (short)Math.Round(Math.Clamp(left, -1, 1) * short.MaxValue);
                stereo[i * Channels + 1] = (short)Math.Round(Math.Clamp(right, -1, 1) * short.MaxValue);
            }
        }
    }
}
