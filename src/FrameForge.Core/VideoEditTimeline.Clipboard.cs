namespace FrameForge.Core;

public sealed record VideoEditCopy(IReadOnlyList<VideoEditTrack> Tracks, bool WholeTracks = false, bool Range = false)
{
    public string Format { get; init; } = "FrameForgeEditorCopy";
    public int Version { get; init; } = 1;
    public bool IsValid => Format == "FrameForgeEditorCopy" && Version == 1 && Tracks is { Count: > 0 and <= 128 }
        && Tracks.All(track => track != null && Enum.IsDefined(track.Kind) && track.Name is { Length: > 0 and <= 256 } && track.Clips != null)
        && Tracks.Sum(track => track.Clips.Count) <= 10000
        && Tracks.All(track => NoOverlap(track.Clips))
        && Tracks.All(track => track.Kind != VideoEditTrackKind.Audio || track.Clips.All(clip => clip != null && !clip.IsStill))
        && Tracks.SelectMany(track => track.Clips).All(clip => clip != null && clip.HasValidVisual && (clip.Kind == VideoEditClipKind.Text || !string.IsNullOrWhiteSpace(clip.SourcePath))
            && clip.Start >= TimeSpan.Zero && clip.Duration >= VideoEditTimeline.MinimumDuration && clip.End <= clip.OriginalDuration
            && clip.OriginalDuration <= TimeSpan.FromDays(7) && clip.Position >= TimeSpan.Zero && clip.Position + clip.Duration <= TimeSpan.FromDays(7)
            && clip.Transform?.IsValid == true && double.IsFinite(clip.Volume) && clip.Volume is >= 0 and <= 1
            && clip.FadeIn >= TimeSpan.Zero && clip.FadeOut >= TimeSpan.Zero && clip.FadeIn + clip.FadeOut <= clip.Duration
            && clip.FadeIn <= TimeSpan.FromSeconds(5) && clip.FadeOut <= TimeSpan.FromSeconds(5));
    static bool NoOverlap(IReadOnlyList<VideoEditClip> clips)
    {
        if (clips.Any(clip => clip == null)) return false;
        TimeSpan end = TimeSpan.Zero;
        foreach (VideoEditClip clip in clips.OrderBy(clip => clip.Position))
        { if (clip.Position < end) return false; end = clip.Position + clip.Duration; }
        return true;
    }
}

public sealed partial class VideoEditTimeline
{
    public VideoEditCopy? CopySelection(bool wholeTrack = false)
    {
        if (!wholeTrack && SelectedClip == null) return null;
        IEnumerable<VideoEditClip> clips = wholeTrack ? SelectedClips : [SelectedClip! with { Position = TimeSpan.Zero }];
        var lane = new VideoEditTrack(SelectedTrackId ?? Guid.Empty, SelectedTrack?.Kind ?? VideoEditTrackKind.Video,
            SelectedTrack?.Name ?? Loc.Get("EditorVideoTrack"), clips.Select(clip => clip.Kind == VideoEditClipKind.Text ? clip : clip with { SourcePath = clip.SourcePath ?? SourcePath }).ToArray())
        { Muted = SelectedTrack?.Muted ?? MainMuted };
        return new([lane], WholeTracks: wholeTrack);
    }

    public VideoEditCopy? CopyRange(TimeSpan start, TimeSpan end)
    {
        if (start < TimeSpan.Zero || end - start < MinimumDuration) return null;
        VideoEditClip[] Capture(IReadOnlyList<VideoEditClip> clips) => clips
            .Where(clip => clip.Position < end && clip.Position + clip.Duration > start)
            .Select(clip => Slice(clip, Max(start, clip.Position), Min(end, clip.Position + clip.Duration), keepId: true))
            .Where(clip => clip.Duration >= MinimumDuration)
            .Select(clip => clip with { SourcePath = clip.Kind == VideoEditClipKind.Text ? null : clip.SourcePath ?? SourcePath, Position = clip.Position - start }).ToArray();
        var lanes = new List<VideoEditTrack>();
        VideoEditClip[] main = Capture(Clips);
        if (main.Length > 0) lanes.Add(new(Guid.Empty, VideoEditTrackKind.Video, Loc.Get("EditorVideoTrack"), main) { Muted = MainMuted });
        foreach (VideoEditTrack track in _tracks)
        {
            VideoEditClip[] clips = Capture(track.Clips);
            if (clips.Length > 0) lanes.Add(track with { Clips = clips });
        }
        return lanes.Count == 0 ? null : new(lanes.OrderBy(lane => _layerOrder.IndexOf(lane.Id)).ToArray(), Range: true);
    }

    public bool RemoveRange(TimeSpan start, TimeSpan end)
    {
        if (start < TimeSpan.Zero || end - start < MinimumDuration ||
            !Clips.Concat(_tracks.SelectMany(track => track.Clips)).Any(clip => clip.Position < end && clip.Position + clip.Duration > start)) return false;
        Guid? selection = SelectedClip?.Id;
        Remember();
        VideoEditClip[] Remove(IReadOnlyList<VideoEditClip> clips)
        {
            var result = new List<VideoEditClip>();
            foreach (VideoEditClip clip in clips)
            {
                TimeSpan finish = clip.Position + clip.Duration;
                if (clip.Position >= end || finish <= start) { result.Add(clip); continue; }
                bool left = start - clip.Position >= MinimumDuration;
                if (left) result.Add(Slice(clip, clip.Position, start, keepId: true));
                if (finish - end >= MinimumDuration) result.Add(Slice(clip, end, finish, keepId: !left));
            }
            return result.ToArray();
        }
        VideoEditClip[] main = Remove(_clips); _clips.Clear(); _clips.AddRange(main);
        for (int i = 0; i < _tracks.Count; i++) _tracks[i] = _tracks[i] with { Clips = Remove(_tracks[i].Clips) };
        SelectedIndex = SelectedClips.ToList().FindIndex(clip => clip.Id == selection);
        if (SelectedIndex < 0 && SelectedClips.Count > 0) SelectedIndex = 0;
        return true;
    }

    public bool Paste(VideoEditCopy copy, TimeSpan at, bool forceNewTracks = false)
    {
        if (!copy.IsValid || at < TimeSpan.Zero || at > TimeSpan.FromDays(7)) return false;
        bool whole = copy.WholeTracks || forceNewTracks;
        var staged = copy.Tracks.Select(lane => lane with
        {
            Clips = lane.Clips.Select(clip => clip with { Id = Guid.NewGuid(), Position = clip.Position + (copy.WholeTracks ? TimeSpan.Zero : at) }).ToArray()
        }).ToArray();
        if (staged.SelectMany(lane => lane.Clips).Any(clip => clip.Position + clip.Duration > TimeSpan.FromDays(7))) return false;
        Remember();
        int inserted = 0;
        foreach (VideoEditTrack lane in staged)
        {
            Guid? targetId = copy.Range ? lane.Id == Guid.Empty ? null : lane.Id : SelectedTrackId;
            VideoEditTrack? target = _tracks.FirstOrDefault(track => track.Id == targetId);
            bool main = targetId == null && lane.Kind == VideoEditTrackKind.Video && lane.Clips.All(clip => !clip.IsStill);
            IReadOnlyList<VideoEditClip>? existing = main ? Clips : target?.Kind == lane.Kind ? target.Clips : null;
            bool collision = existing != null && lane.Clips.Any(clip => existing.Any(other => clip.Position < other.Position + other.Duration && clip.Position + clip.Duration > other.Position));
            Guid? selected;
            if (whole || existing == null || collision)
            {
                var added = lane with { Id = Guid.NewGuid(), Name = Loc.Format("EditorCopiedTrackName", lane.Name[..Math.Min(230, lane.Name.Length)]) };
                _tracks.Add(added); _layerOrder.Insert(inserted++, added.Id); selected = added.Id;
            }
            else if (main)
            {
                _clips.AddRange(lane.Clips); _clips.Sort((a, b) => a.Position.CompareTo(b.Position)); selected = null;
            }
            else
            {
                ReplaceTrack(target! with { Clips = target!.Clips.Concat(lane.Clips).OrderBy(clip => clip.Position).ToArray() }); selected = target.Id;
            }
            SelectedTrackId = selected;
            Guid? first = lane.Clips.FirstOrDefault()?.Id;
            SelectedIndex = SelectedClips.ToList().FindIndex(clip => clip.Id == first);
        }
        return true;
    }

    static VideoEditClip Slice(VideoEditClip clip, TimeSpan start, TimeSpan end, bool keepId)
    {
        TimeSpan sourceStart = clip.Start + start - clip.Position, sourceEnd = clip.Start + end - clip.Position;
        return clip with
        {
            Id = keepId ? clip.Id : Guid.NewGuid(), Start = sourceStart, End = sourceEnd, Position = start,
            FadeIn = start == clip.Position ? Min(clip.FadeIn, end - start) : TimeSpan.Zero,
            FadeOut = end == clip.Position + clip.Duration ? Min(clip.FadeOut, end - start) : TimeSpan.Zero
        };
    }

    static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
}
