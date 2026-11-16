namespace FrameForge.Core;

/// <summary>A linked video/audio interval in the original recording. Edits never modify the source.</summary>
public sealed record VideoEditClip(Guid Id, TimeSpan Start, TimeSpan End)
{
    public TimeSpan Duration => End - Start;
    public string? SourcePath { get; init; }
    public TimeSpan OriginalDuration { get; init; }
    /// <summary>Absolute timeline position, independent of every other clip.</summary>
    public TimeSpan Position { get; init; }
    public VideoFrameTransform Transform { get; init; } = new();
    public double Volume { get; init; } = 1;
    public TimeSpan FadeIn { get; init; }
    public TimeSpan FadeOut { get; init; }
    public double OpacityAt(TimeSpan time)
    {
        double opacity = 1;
        if (FadeIn > TimeSpan.Zero) opacity = Math.Min(opacity, time.TotalSeconds / FadeIn.TotalSeconds);
        if (FadeOut > TimeSpan.Zero) opacity = Math.Min(opacity, (Duration - time).TotalSeconds / FadeOut.TotalSeconds);
        return Math.Clamp(opacity, 0, 1);
    }
}

public readonly record struct VideoEditPosition(int ClipIndex, TimeSpan SourceTime, TimeSpan ClipStart);
public enum VideoEditTrackKind { Video, Audio }
public sealed record VideoEditTrack(Guid Id, VideoEditTrackKind Kind, string Name, IReadOnlyList<VideoEditClip> Clips)
{
    public bool Muted { get; init; }
}

/// <summary>Crop is expressed as fractions of the unrotated source; position is relative to the output canvas.</summary>
public sealed record VideoFrameTransform
{
    public double Left { get; init; }
    public double Top { get; init; }
    public double Right { get; init; }
    public double Bottom { get; init; }
    public int QuarterTurns { get; init; }
    public double Scale { get; init; } = 1;
    public double OffsetX { get; init; }
    public double OffsetY { get; init; }
    public bool IsValid => new[] { Left, Top, Right, Bottom, Scale, OffsetX, OffsetY }.All(double.IsFinite)
        && Left >= 0 && Top >= 0 && Right >= 0 && Bottom >= 0 && Left + Right <= .95 && Top + Bottom <= .95
        && QuarterTurns is >= 0 and <= 3 && Scale is >= .1 and <= 4 && Math.Abs(OffsetX) <= 1 && Math.Abs(OffsetY) <= 1;
}

public sealed record VideoEditSnapshot(string? SourcePath, TimeSpan SourceDuration,
    IReadOnlyList<VideoEditClip> Clips, IReadOnlyList<VideoEditTrack> Tracks, bool MainMuted)
{
    public TimeSpan MainDuration => TimeSpan.FromTicks(Clips.Select(clip => (clip.Position + clip.Duration).Ticks).DefaultIfEmpty(0).Max());
    public TimeSpan Duration => TimeSpan.FromTicks(Math.Max(MainDuration.Ticks,
        Tracks.SelectMany(track => track.Clips).Select(clip => (clip.Position + clip.Duration).Ticks).DefaultIfEmpty(0).Max()));
}

/// <summary>Independently positioned clips on every track. Trimming or deleting never moves neighboring clips.</summary>
public sealed partial class VideoEditTimeline
{
    public static readonly TimeSpan MinimumDuration = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / 30);
    readonly List<VideoEditClip> _clips = [];
    readonly List<VideoEditTrack> _tracks = [];
    readonly Stack<State> _undo = new();
    readonly Stack<State> _redo = new();
    readonly record struct State(VideoEditSnapshot Snapshot, int SelectedIndex, Guid? SelectedTrackId, Guid Id);
    readonly Guid _initialState = Guid.NewGuid();

    public VideoEditTimeline(TimeSpan sourceDuration, string? sourcePath = null)
    {
        if (sourceDuration < MinimumDuration) throw new ArgumentOutOfRangeException(nameof(sourceDuration));
        SourceDuration = sourceDuration;
        SourcePath = sourcePath;
        StateId = _initialState;
        _clips.Add(new VideoEditClip(Guid.NewGuid(), TimeSpan.Zero, sourceDuration) { SourcePath = sourcePath, OriginalDuration = sourceDuration });
        Clips = _clips.AsReadOnly();
        Tracks = _tracks.AsReadOnly();
    }

    public IReadOnlyList<VideoEditClip> Clips { get; }
    public TimeSpan SourceDuration { get; }
    public string? SourcePath { get; }
    public IReadOnlyList<VideoEditTrack> Tracks { get; }
    public bool MainMuted { get; private set; }
    public Guid StateId { get; private set; }
    public TimeSpan MainDuration => TimeSpan.FromTicks(_clips.Select(clip => (clip.Position + clip.Duration).Ticks).DefaultIfEmpty(0).Max());
    public TimeSpan Duration => TimeSpan.FromTicks(Math.Max(MainDuration.Ticks,
        _tracks.SelectMany(track => track.Clips).Select(clip => (clip.Position + clip.Duration).Ticks).DefaultIfEmpty(0).Max()));
    public int SelectedIndex { get; set; }
    public Guid? SelectedTrackId { get; set; }
    public VideoEditTrack? SelectedTrack => _tracks.FirstOrDefault(track => track.Id == SelectedTrackId);
    public IReadOnlyList<VideoEditClip> SelectedClips => SelectedTrack?.Clips ?? Clips;
    public VideoEditClip? SelectedClip => SelectedIndex >= 0 && SelectedIndex < SelectedClips.Count ? SelectedClips[SelectedIndex] : null;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public bool HasChanges => StateId != _initialState;

    public TimeSpan StartOf(int index) => index >= 0 && index < _clips.Count ? _clips[index].Position : MainDuration;
    public TimeSpan SelectedStart => SelectedTrackId == null ? StartOf(SelectedIndex) : SelectedClip?.Position ?? TimeSpan.Zero;
    public TimeSpan SourceLength(VideoEditClip clip) => clip.OriginalDuration > TimeSpan.Zero ? clip.OriginalDuration : SourceDuration;
    public VideoEditSnapshot Snapshot() => new(SourcePath, SourceDuration, _clips.ToArray(), _tracks.ToArray(), MainMuted);

    public static VideoEditTimeline FromSnapshot(VideoEditSnapshot snapshot)
    {
        var timeline = new VideoEditTimeline(snapshot.SourceDuration, snapshot.SourcePath);
        timeline._clips.Clear(); timeline._clips.AddRange(snapshot.Clips);
        timeline._tracks.AddRange(snapshot.Tracks);
        timeline.MainMuted = snapshot.MainMuted;
        timeline.SelectedIndex = timeline.Clips.Count > 0 ? 0 : -1;
        return timeline;
    }

    public VideoEditPosition? Resolve(TimeSpan position)
    {
        if (_clips.Count == 0) return null;
        long time = Math.Max(0, position.Ticks);
        bool finalFrame = position == MainDuration && MainDuration == Duration;
        for (int index = 0; index < _clips.Count; index++)
        {
            VideoEditClip clip = _clips[index];
            long local = time - clip.Position.Ticks;
            if (local >= 0 && (local < clip.Duration.Ticks || finalFrame && local == clip.Duration.Ticks))
                return new(index, clip.Start + TimeSpan.FromTicks(Math.Min(local, clip.Duration.Ticks - 1)), clip.Position);
        }
        return null;
    }

    public bool Split(TimeSpan position)
    {
        if (SelectedTrackId != null) return SplitOnTrack(position);
        if (Resolve(position) is not { } resolved) return false;
        VideoEditClip clip = _clips[resolved.ClipIndex];
        TimeSpan cut = resolved.SourceTime;
        if (cut - clip.Start < MinimumDuration || clip.End - cut < MinimumDuration) return false;
        Remember();
        _clips[resolved.ClipIndex] = clip with { End = cut, FadeIn = Min(clip.FadeIn, cut - clip.Start), FadeOut = TimeSpan.Zero };
        _clips.Insert(resolved.ClipIndex + 1, clip with { Id = Guid.NewGuid(), Start = cut, Position = position, FadeIn = TimeSpan.Zero, FadeOut = Min(clip.FadeOut, clip.End - cut) });
        SelectedIndex = resolved.ClipIndex + 1;
        return true;
    }

    public bool Trim(int index, TimeSpan start, TimeSpan end)
    {
        IReadOnlyList<VideoEditClip> clips = SelectedClips;
        if (index < 0 || index >= clips.Count || start < TimeSpan.Zero || end > SourceLength(clips[index]) || end - start < MinimumDuration)
            return false;
        VideoEditClip clip = clips[index];
        if (clip.Start == start && clip.End == end) return false;
        TimeSpan position = clip.Position + start - clip.Start;
        if (position < TimeSpan.Zero || clips.Any(other => other.Id != clip.Id &&
            position < other.Position + other.Duration && position + end - start > other.Position)) return false;
        Remember();
        double fadeScale = Math.Min(1, (end - start).TotalSeconds / Math.Max(.00001, (clip.FadeIn + clip.FadeOut).TotalSeconds));
        TimeSpan fadeIn = clip.FadeIn * fadeScale, fadeOut = clip.FadeOut * fadeScale;
        if (fadeIn + fadeOut > end - start) fadeOut = end - start - fadeIn;
        ReplaceSelected(index, clip with { Start = start, End = end, Position = position, FadeIn = fadeIn, FadeOut = fadeOut });
        SelectedIndex = index;
        return true;
    }

    public bool DeleteSelected()
    {
        if (SelectedClip == null) return false;
        Remember();
        if (SelectedTrack is { } track)
            ReplaceTrack(track with { Clips = track.Clips.Where((_, index) => index != SelectedIndex).ToArray() });
        else _clips.RemoveAt(SelectedIndex);
        SelectedIndex = Math.Min(SelectedIndex, SelectedClips.Count - 1);
        return true;
    }

    public bool Undo() => Restore(_undo, _redo);
    public bool Redo() => Restore(_redo, _undo);

    public Guid AddTrack(VideoEditTrackKind kind, string name)
    {
        Remember();
        var track = new VideoEditTrack(Guid.NewGuid(), kind, name, Array.Empty<VideoEditClip>());
        _tracks.Add(track);
        SelectedTrackId = track.Id;
        SelectedIndex = -1;
        return track.Id;
    }

    public bool RemoveSelectedTrack()
    {
        if (SelectedTrack == null) return false;
        Remember();
        _tracks.RemoveAll(track => track.Id == SelectedTrackId);
        SelectedTrackId = null;
        SelectedIndex = _clips.Count > 0 ? 0 : -1;
        return true;
    }

    public bool ToggleMute(Guid? trackId)
    {
        VideoEditTrack? track = _tracks.FirstOrDefault(track => track.Id == trackId);
        if (trackId != null && track == null) return false;
        Remember();
        if (track == null) MainMuted = !MainMuted;
        else ReplaceTrack(track with { Muted = !track.Muted });
        return true;
    }

    public bool AddSource(string path, TimeSpan duration, TimeSpan position, VideoEditTrackKind kind)
    {
        if (string.IsNullOrWhiteSpace(path) || duration < MinimumDuration || position < TimeSpan.Zero) return false;
        if (kind == VideoEditTrackKind.Audio && SelectedTrack?.Kind != VideoEditTrackKind.Audio ||
            kind == VideoEditTrackKind.Video && SelectedTrack?.Kind == VideoEditTrackKind.Audio) return false;
        Remember();
        TimeSpan at = position;
        foreach (VideoEditClip other in SelectedClips.OrderBy(clip => clip.Position))
            if (at < other.Position + other.Duration && at + duration > other.Position) at = other.Position + other.Duration;
        var clip = new VideoEditClip(Guid.NewGuid(), TimeSpan.Zero, duration)
        { SourcePath = path, OriginalDuration = duration, Position = at };
        if (SelectedTrack is { } track) ReplaceTrack(track with { Clips = track.Clips.Append(clip).OrderBy(clip => clip.Position).ToArray() });
        else { _clips.Add(clip); _clips.Sort((a, b) => a.Position.CompareTo(b.Position)); }
        SelectedIndex = SelectedClips.ToList().FindIndex(item => item.Id == clip.Id);
        return true;
    }

    public bool SetSelectedPosition(TimeSpan position)
    {
        if (SelectedClip is not { } clip || position < TimeSpan.Zero || clip.Position == position) return false;
        if (SelectedClips.Any(other => other.Id != clip.Id && position < other.Position + other.Duration && position + clip.Duration > other.Position)) return false;
        Remember();
        if (SelectedTrack is { } track)
            ReplaceTrack(track with { Clips = track.Clips.Select(item => item.Id == clip.Id ? item with { Position = position } : item).OrderBy(item => item.Position).ToArray() });
        else
        {
            _clips[SelectedIndex] = clip with { Position = position };
            _clips.Sort((a, b) => a.Position.CompareTo(b.Position));
        }
        SelectedIndex = SelectedClips.ToList().FindIndex(item => item.Id == clip.Id);
        return true;
    }

    public (TimeSpan MinimumStart, TimeSpan MaximumEnd) TrimBounds(int index)
    {
        if (index < 0 || index >= SelectedClips.Count) return (TimeSpan.Zero, TimeSpan.Zero);
        VideoEditClip clip = SelectedClips[index];
        long minimum = Math.Max(0, clip.Start.Ticks - clip.Position.Ticks), maximum = SourceLength(clip).Ticks;
        foreach (VideoEditClip other in SelectedClips.Where(other => other.Id != clip.Id))
        {
            if (other.Position + other.Duration <= clip.Position)
                minimum = Math.Max(minimum, clip.Start.Ticks + (other.Position + other.Duration - clip.Position).Ticks);
            if (other.Position >= clip.Position + clip.Duration)
                maximum = Math.Min(maximum, clip.Start.Ticks + (other.Position - clip.Position).Ticks);
        }
        return (TimeSpan.FromTicks(minimum), TimeSpan.FromTicks(maximum));
    }

    public bool CanPlaceSelected(TimeSpan position) => SelectedClip is { } clip && position >= TimeSpan.Zero &&
        !SelectedClips.Any(other => other.Id != clip.Id && position < other.Position + other.Duration && position + clip.Duration > other.Position);

    public bool SetSelectedTransform(VideoFrameTransform transform)
    {
        if (SelectedClip is not { } clip || SelectedTrack?.Kind == VideoEditTrackKind.Audio || !transform.IsValid || clip.Transform == transform) return false;
        Remember();
        ReplaceSelected(SelectedIndex, clip with { Transform = transform });
        return true;
    }

    public bool SetSelectedVolume(double volume)
    {
        if (SelectedClip is not { } clip || !double.IsFinite(volume) || volume is < 0 or > 1 || clip.Volume == volume) return false;
        Remember();
        ReplaceSelected(SelectedIndex, clip with { Volume = volume });
        return true;
    }

    public bool SetSelectedFades(TimeSpan fadeIn, TimeSpan fadeOut)
    {
        if (SelectedClip is not { } clip || SelectedTrack?.Kind == VideoEditTrackKind.Audio || fadeIn < TimeSpan.Zero || fadeOut < TimeSpan.Zero ||
            fadeIn + fadeOut > clip.Duration || fadeIn > TimeSpan.FromSeconds(5) || fadeOut > TimeSpan.FromSeconds(5) ||
            clip.FadeIn == fadeIn && clip.FadeOut == fadeOut) return false;
        Remember(); ReplaceSelected(SelectedIndex, clip with { FadeIn = fadeIn, FadeOut = fadeOut }); return true;
    }

    static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    bool SplitOnTrack(TimeSpan position)
    {
        if (SelectedTrack is not { } track) return false;
        int index = track.Clips.ToList().FindIndex(clip => position >= clip.Position && position < clip.Position + clip.Duration);
        if (index < 0) return false;
        VideoEditClip clip = track.Clips[index];
        TimeSpan cut = clip.Start + position - clip.Position;
        if (cut - clip.Start < MinimumDuration || clip.End - cut < MinimumDuration) return false;
        Remember();
        var clips = track.Clips.ToList();
        clips[index] = clip with { End = cut, FadeIn = Min(clip.FadeIn, cut - clip.Start), FadeOut = TimeSpan.Zero };
        clips.Insert(index + 1, clip with { Id = Guid.NewGuid(), Start = cut, Position = position, FadeIn = TimeSpan.Zero, FadeOut = Min(clip.FadeOut, clip.End - cut) });
        ReplaceTrack(track with { Clips = clips.ToArray() });
        SelectedIndex = index + 1;
        return true;
    }

    void ReplaceSelected(int index, VideoEditClip clip)
    {
        if (SelectedTrack is { } track) ReplaceTrack(track with { Clips = track.Clips.Select((item, i) => i == index ? clip : item).ToArray() });
        else _clips[index] = clip;
    }

    void ReplaceTrack(VideoEditTrack track) => _tracks[_tracks.FindIndex(item => item.Id == track.Id)] = track;

    void Remember()
    {
        _undo.Push(new(Snapshot(), SelectedIndex, SelectedTrackId, StateId));
        _redo.Clear();
        StateId = Guid.NewGuid();
    }

    bool Restore(Stack<State> from, Stack<State> to)
    {
        if (!from.TryPop(out State state)) return false;
        to.Push(new(Snapshot(), SelectedIndex, SelectedTrackId, StateId));
        _clips.Clear();
        _clips.AddRange(state.Snapshot.Clips);
        _tracks.Clear();
        _tracks.AddRange(state.Snapshot.Tracks);
        MainMuted = state.Snapshot.MainMuted;
        SelectedIndex = state.SelectedIndex;
        SelectedTrackId = state.SelectedTrackId;
        StateId = state.Id;
        return true;
    }
}
