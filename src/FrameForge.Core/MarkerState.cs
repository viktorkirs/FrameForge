namespace FrameForge.Core;

public readonly record struct MarkerPoint(float X, float Y);
public readonly record struct MarkerSegment(MarkerPoint Start, MarkerPoint End, float Width);
public sealed record MarkerSnapshot(long Version, MarkerSegment[] Segments);

public sealed class MarkerState
{
    readonly object _gate = new();
    readonly List<MarkerSegment> _segments = [];
    MarkerPoint? _last;
    float _width;
    bool _active;
    long _version;
    MarkerSnapshot? _cached;
    public event Action? Changed;

    public void SetActive(bool active)
    {
        lock (_gate)
        {
            if (_active == active) return;
            _active = active;
            if (!active) { _segments.Clear(); _last = null; }
            _version++;
            _cached = null;
        }
        Changed?.Invoke();
    }

    public void BeginStroke(float x, float y, float width)
    {
        if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(width)) return;
        lock (_gate)
        {
            if (!_active) return;
            _last = new MarkerPoint(x, y);
            _width = Math.Clamp(width, 2, 64);
            Append(new MarkerSegment(_last.Value, _last.Value, _width));
        }
        Changed?.Invoke();
    }

    public void AddPoint(float x, float y)
    {
        if (!float.IsFinite(x) || !float.IsFinite(y)) return;
        lock (_gate)
        {
            if (!_active || _last is not MarkerPoint previous) return;
            float dx = x - previous.X, dy = y - previous.Y;
            if (dx * dx + dy * dy < 4) return;
            var next = new MarkerPoint(x, y);
            Append(new MarkerSegment(previous, next, _width));
            _last = next;
        }
        Changed?.Invoke();
    }

    public void EndStroke() { lock (_gate) _last = null; }

    void Append(MarkerSegment segment)
    {
        if (_segments.Count >= 8192) _segments.RemoveRange(0, 1024);
        _segments.Add(segment);
        _version++;
        _cached = null;
    }

    public MarkerSnapshot Snapshot()
    {
        lock (_gate) return _cached ??= new MarkerSnapshot(_version, _segments.ToArray());
    }
}
