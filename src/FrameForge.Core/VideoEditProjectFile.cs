using System.Text.Json;
using System.Text.Json.Serialization;

namespace FrameForge.Core;

public sealed record VideoEditProject(VideoEditSnapshot Timeline, uint Width, uint Height, double FrameRate)
{
    public string Format { get; init; } = "FrameForgeProject";
    public int Version { get; init; } = 2;
    public TimeSpan Playhead { get; init; }
    public double Zoom { get; init; } = 1;
    public double TrackHeightScale { get; init; } = 1;
}

/// <summary>Versioned, atomic project files. Media remains external; relative paths make a folder portable.</summary>
public static class VideoEditProjectFile
{
    public const string Extension = ".ffproj";
    const int MaximumBytes = 16 * 1024 * 1024;
    static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true, IgnoreReadOnlyProperties = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static void Save(string path, VideoEditProject project)
    {
        Validate(project);
        if (project.Version != 2) throw new InvalidDataException(Loc.Get("EditorProjectInvalid"));
        string destination = Path.GetFullPath(path);
        if (!string.Equals(Path.GetExtension(destination), Extension, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(Loc.Get("EditorProjectExtension"));
        if (MediaPaths(project.Timeline).Any(source => string.Equals(Path.GetFullPath(source), destination, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException(Loc.Get("EditorProtectSource"));
        string directory = Path.GetDirectoryName(destination)!;
        VideoEditProject relative = MapPaths(project, source => Path.GetRelativePath(directory, Path.GetFullPath(source)));
        string json = JsonSerializer.Serialize(relative, Options);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaximumBytes) throw new InvalidDataException(Loc.Get("EditorProjectInvalid"));
        string temporary = Path.Combine(directory, $".frameforge-project-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, json);
            File.Move(temporary, destination, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static VideoEditProject Load(string path)
    {
        string fullPath = Path.GetFullPath(path);
        if (new FileInfo(fullPath).Length > MaximumBytes) throw new InvalidDataException(Loc.Get("EditorProjectInvalid"));
        try
        {
            VideoEditProject project = JsonSerializer.Deserialize<VideoEditProject>(File.ReadAllText(fullPath), Options)
                ?? throw new InvalidDataException(Loc.Get("EditorProjectInvalid"));
            Validate(project);
            if (project.Version == 1)
            {
                // Version 1 treated the main track as contiguous and ignored Position.
                TimeSpan at = TimeSpan.Zero;
                VideoEditClip[] positioned = project.Timeline.Clips.Select(clip =>
                {
                    VideoEditClip result = clip with { Position = at }; at += clip.Duration; return result;
                }).ToArray();
                project = project with { Version = 2, Timeline = project.Timeline with { Clips = positioned } };
                Validate(project);
            }
            string directory = Path.GetDirectoryName(fullPath)!;
            return MapPaths(project, source => Path.GetFullPath(source, directory));
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or OverflowException)
        { throw new InvalidDataException(Loc.Get("EditorProjectInvalid"), ex); }
    }

    public static IEnumerable<string> MediaPaths(VideoEditSnapshot snapshot) => snapshot.Clips.Concat(snapshot.Tracks.SelectMany(track => track.Clips))
        .Select(clip => clip.SourcePath ?? snapshot.SourcePath!).Distinct(StringComparer.OrdinalIgnoreCase);

    static VideoEditProject MapPaths(VideoEditProject project, Func<string, string> map)
    {
        VideoEditSnapshot snapshot = project.Timeline;
        VideoEditClip MapClip(VideoEditClip clip) => clip with { SourcePath = map(clip.SourcePath ?? snapshot.SourcePath!) };
        return project with
        {
            Timeline = snapshot with
            {
                SourcePath = snapshot.SourcePath == null ? null : map(snapshot.SourcePath),
                Clips = snapshot.Clips.Select(MapClip).ToArray(),
                Tracks = snapshot.Tracks.Select(track => track with { Clips = track.Clips.Select(MapClip).ToArray() }).ToArray()
            }
        };
    }

    public static void Validate(VideoEditProject project)
    {
        if (project.Format != "FrameForgeProject" || project.Version is not (1 or 2) || project.Timeline is not { } snapshot ||
            project.Width is < 2 or > 16384 || project.Height is < 2 or > 16384 || project.Width % 2 != 0 || project.Height % 2 != 0 ||
            !double.IsFinite(project.FrameRate) || project.FrameRate is < 1 or > 120 || project.Playhead < TimeSpan.Zero ||
            !double.IsFinite(project.Zoom) || project.Zoom is < .1 or > 24 || !double.IsFinite(project.TrackHeightScale) ||
            project.TrackHeightScale is < .45 or > 1.75 || snapshot.SourceDuration < VideoEditTimeline.MinimumDuration ||
            snapshot.SourceDuration > TimeSpan.FromDays(7) || snapshot.Clips == null || snapshot.Tracks == null || snapshot.Tracks.Count > 128)
            throw new InvalidDataException(Loc.Get("EditorProjectInvalid"));
        if (snapshot.Tracks.Any(track => track == null || track.Id == Guid.Empty || !Enum.IsDefined(track.Kind) ||
            string.IsNullOrWhiteSpace(track.Name) || track.Name.Length > 256 || track.Clips == null) ||
            snapshot.Tracks.Select(track => track.Id).Distinct().Count() != snapshot.Tracks.Count)
            throw new InvalidDataException(Loc.Get("EditorProjectInvalid"));
        VideoEditClip[] clips = snapshot.Clips.Concat(snapshot.Tracks.SelectMany(track => track.Clips)).ToArray();
        if (clips.Length > 10000 || clips.Any(clip => clip == null || clip.Id == Guid.Empty ||
            string.IsNullOrWhiteSpace(clip.SourcePath ?? snapshot.SourcePath) || clip.Start < TimeSpan.Zero ||
            clip.OriginalDuration < VideoEditTimeline.MinimumDuration || clip.OriginalDuration > TimeSpan.FromDays(7) ||
            clip.End > clip.OriginalDuration || clip.Duration < VideoEditTimeline.MinimumDuration ||
            clip.Position < TimeSpan.Zero || clip.Position > TimeSpan.FromDays(7) || clip.Transform?.IsValid != true ||
            clip.FadeIn < TimeSpan.Zero || clip.FadeOut < TimeSpan.Zero || clip.FadeIn + clip.FadeOut > clip.Duration ||
            clip.FadeIn > TimeSpan.FromSeconds(5) || clip.FadeOut > TimeSpan.FromSeconds(5) ||
            !double.IsFinite(clip.Volume) || clip.Volume is < 0 or > 1) || clips.Select(clip => clip.Id).Distinct().Count() != clips.Length)
            throw new InvalidDataException(Loc.Get("EditorProjectInvalid"));
        IEnumerable<IReadOnlyList<VideoEditClip>> lanes = snapshot.Tracks.Select(track => track.Clips);
        if (project.Version >= 2) lanes = lanes.Prepend(snapshot.Clips);
        foreach (IReadOnlyList<VideoEditClip> lane in lanes)
        {
            TimeSpan end = TimeSpan.Zero;
            foreach (VideoEditClip clip in lane.OrderBy(clip => clip.Position))
            {
                if (clip.Position < end) throw new InvalidDataException(Loc.Get("EditorProjectInvalid"));
                end = clip.Position + clip.Duration;
            }
        }
        if (snapshot.Duration > TimeSpan.FromDays(7)) throw new InvalidDataException(Loc.Get("EditorProjectInvalid"));
    }
}
