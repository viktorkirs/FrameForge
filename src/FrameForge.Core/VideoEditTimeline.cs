namespace FrameForge.Core;

/// <summary>A linked video/audio interval in the original recording. Edits never modify the source.</summary>
public sealed record VideoEditClip(Guid Id, TimeSpan Start, TimeSpan End)
{
    public TimeSpan Duration => End - Start;
}

public readonly record struct VideoEditPosition(int ClipIndex, TimeSpan SourceTime, TimeSpan ClipStart);

/// <summary>A contiguous, single-source montage. Removing and trimming clips closes the gap.</summary>
public sealed class VideoEditTimeline
{
    public static readonly TimeSpan MinimumDuration = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / 30);
    readonly List<VideoEditClip> _clips = [];
    readonly Stack<State> _undo = new();
    readonly Stack<State> _redo = new();
    readonly record struct State(VideoEditClip[] Clips, int SelectedIndex);

    public VideoEditTimeline(TimeSpan sourceDuration)
    {
        if (sourceDuration < MinimumDuration) throw new ArgumentOutOfRangeException(nameof(sourceDuration));
        SourceDuration = sourceDuration;
        _clips.Add(new VideoEditClip(Guid.NewGuid(), TimeSpan.Zero, sourceDuration));
        Clips = _clips.AsReadOnly();
    }

    public IReadOnlyList<VideoEditClip> Clips { get; }
    public TimeSpan SourceDuration { get; }
    public TimeSpan Duration => TimeSpan.FromTicks(_clips.Sum(clip => clip.Duration.Ticks));
    public int SelectedIndex { get; set; }
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public bool HasChanges => _clips.Count != 1 || _clips[0].Start != TimeSpan.Zero || _clips[0].End != SourceDuration;

    public TimeSpan StartOf(int index) => TimeSpan.FromTicks(_clips.Take(index).Sum(clip => clip.Duration.Ticks));

    public VideoEditPosition? Resolve(TimeSpan position)
    {
        if (_clips.Count == 0) return null;
        long remaining = Math.Clamp(position.Ticks, 0, Duration.Ticks);
        long start = 0;
        for (int index = 0; index < _clips.Count; index++)
        {
            VideoEditClip clip = _clips[index];
            if (remaining < clip.Duration.Ticks || index == _clips.Count - 1)
                return new(index, clip.Start + TimeSpan.FromTicks(Math.Min(remaining, clip.Duration.Ticks - 1)), TimeSpan.FromTicks(start));
            remaining -= clip.Duration.Ticks;
            start += clip.Duration.Ticks;
        }
        return null;
    }

    public bool Split(TimeSpan position)
    {
        if (Resolve(position) is not { } resolved) return false;
        VideoEditClip clip = _clips[resolved.ClipIndex];
        TimeSpan cut = resolved.SourceTime;
        if (cut - clip.Start < MinimumDuration || clip.End - cut < MinimumDuration) return false;
        Remember();
        _clips[resolved.ClipIndex] = clip with { End = cut };
        _clips.Insert(resolved.ClipIndex + 1, new VideoEditClip(Guid.NewGuid(), cut, clip.End));
        SelectedIndex = resolved.ClipIndex + 1;
        return true;
    }

    public bool Trim(int index, TimeSpan start, TimeSpan end)
    {
        if (index < 0 || index >= _clips.Count || start < TimeSpan.Zero || end > SourceDuration || end - start < MinimumDuration)
            return false;
        VideoEditClip clip = _clips[index];
        if (clip.Start == start && clip.End == end) return false;
        Remember();
        _clips[index] = clip with { Start = start, End = end };
        SelectedIndex = index;
        return true;
    }

    public bool DeleteSelected()
    {
        if (SelectedIndex < 0 || SelectedIndex >= _clips.Count) return false;
        Remember();
        _clips.RemoveAt(SelectedIndex);
        SelectedIndex = Math.Min(SelectedIndex, _clips.Count - 1);
        return true;
    }

    /// <param name="destination">The final index after moving the clip.</param>
    public bool Move(int index, int destination)
    {
        if (index < 0 || index >= _clips.Count || destination < 0 || destination >= _clips.Count || index == destination) return false;
        Remember();
        VideoEditClip clip = _clips[index];
        _clips.RemoveAt(index);
        _clips.Insert(destination, clip);
        SelectedIndex = destination;
        return true;
    }

    public bool Undo() => Restore(_undo, _redo);
    public bool Redo() => Restore(_redo, _undo);

    void Remember()
    {
        _undo.Push(new(_clips.ToArray(), SelectedIndex));
        _redo.Clear();
    }

    bool Restore(Stack<State> from, Stack<State> to)
    {
        if (!from.TryPop(out State state)) return false;
        to.Push(new(_clips.ToArray(), SelectedIndex));
        _clips.Clear();
        _clips.AddRange(state.Clips);
        SelectedIndex = state.SelectedIndex;
        return true;
    }
}
