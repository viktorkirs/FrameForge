namespace FrameForge.Core;

public sealed class AudioLevelTracker
{
    double _peak;

    public void Report(ReadOnlySpan<float> samples)
    {
        double peak = 0;
        foreach (float sample in samples)
            if (float.IsFinite(sample)) peak = Math.Max(peak, Math.Abs(sample));
        double current = Volatile.Read(ref _peak);
        while (peak > current)
        {
            double previous = Interlocked.CompareExchange(ref _peak, peak, current);
            if (previous == current) break;
            current = previous;
        }
    }

    public double ReadLevel()
    {
        double peak = Interlocked.Exchange(ref _peak, 0);
        return peak <= .0005 ? 0 : Math.Clamp((20 * Math.Log10(peak) + 66) / 66, 0, 1);
    }

    public void Reset() => Interlocked.Exchange(ref _peak, 0);
}
