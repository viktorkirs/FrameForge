using System.IO;
using System.Windows.Media.Imaging;
using FrameForge.Core;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace FrameForge.UI;

public sealed record VideoEditorSource(TimeSpan Duration, uint Width, uint Height, double FrameRate, bool HasAudio);
internal readonly record struct VideoEditorFrameRequest(TimeSpan Position, TimeSpan Start, TimeSpan End);

/// <summary>A source decoder reused for paused frame requests, independent of the playback clock.</summary>
internal sealed class VideoEditorPreview(MediaComposition composition, VideoEditorSource source)
{
    public VideoEditorSource Source { get; } = source;

    public async Task<BitmapSource> ReadFrameAsync(VideoEditorFrameRequest request, CancellationToken cancellationToken)
    {
        // Source frame timestamps are rounded to ticks; allow that rounding at frame boundaries.
        // The out point is exclusive: seeking to the clip's end must show its last retained frame.
        double fps = Math.Max(1, Source.FrameRate);
        double sourceLast = Math.Max(0, Math.Floor(Source.Duration.TotalSeconds * fps - 1 + .00001));
        double first = Math.Min(sourceLast, Math.Max(0, Math.Ceiling(request.Start.TotalSeconds * fps - .00001)));
        double last = Math.Max(first, Math.Min(sourceLast, Math.Ceiling(request.End.TotalSeconds * fps - .00001) - 1));
        double frame = Math.Clamp(Math.Round(request.Position.TotalSeconds * fps), first, last);
        TimeSpan position = TimeSpan.FromSeconds(frame / fps);
        using var image = await composition.GetThumbnailAsync(position, (int)Math.Min(1280, Source.Width), 0, VideoFramePrecision.NearestFrame)
            .AsTask(cancellationToken);
        using Stream stream = image.AsStreamForRead();
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }
}

public static class VideoEditorMedia
{
    public static async Task<BitmapSource> ReadThumbnailAsync(string path, TimeSpan position)
    {
        var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path));
        var composition = new MediaComposition();
        composition.Clips.Add(await MediaClip.CreateFromFileAsync(file));
        using var image = await composition.GetThumbnailAsync(position, 160, 90, VideoFramePrecision.NearestFrame);
        using Stream stream = image.AsStreamForRead();
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    public static async Task<VideoEditorSource> ReadAsync(string path) => (await OpenPreviewAsync(path)).Source;

    internal static async Task<VideoEditorPreview> OpenPreviewAsync(string path)
    {
        var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path));
        var clip = await MediaClip.CreateFromFileAsync(file);
        VideoEncodingProperties video = clip.GetVideoEncodingProperties();
        var source = new VideoEditorSource(clip.OriginalDuration, video.Width, video.Height,
            video.FrameRate.Denominator == 0 ? 30 : video.FrameRate.Numerator / (double)video.FrameRate.Denominator,
            clip.EmbeddedAudioTracks.Count > 0);
        var composition = new MediaComposition();
        composition.Clips.Add(clip);
        return new(composition, source);
    }

    public static async Task ExportAsync(string sourcePath, IReadOnlyList<VideoEditClip> clips, string destinationPath,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        string source = Path.GetFullPath(sourcePath);
        string destination = Path.GetFullPath(destinationPath);
        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(Loc.Get("EditorProtectSource"));
        if (clips.Count == 0) throw new InvalidOperationException(Loc.Get("EditorEmpty"));
        cancellationToken.ThrowIfCancellationRequested();
        var file = await StorageFile.GetFileFromPathAsync(source);
        var original = await MediaClip.CreateFromFileAsync(file);
        var composition = new MediaComposition();
        foreach (VideoEditClip interval in clips)
        {
            if (interval.Start < TimeSpan.Zero || interval.End > original.OriginalDuration || interval.Duration < VideoEditTimeline.MinimumDuration)
                throw new ArgumentOutOfRangeException(nameof(clips));
            var clip = original.Clone();
            clip.TrimTimeFromStart = interval.Start;
            clip.TrimTimeFromEnd = original.OriginalDuration - interval.End;
            composition.Clips.Add(clip);
        }
        // Render beside the destination, then replace it only after a successful, complete export.
        string temporary = Path.Combine(Path.GetDirectoryName(destination)!, $".frameforge-{Guid.NewGuid():N}.mp4");
        try
        {
            using (File.Create(temporary)) { }
            var target = await StorageFile.GetFileFromPathAsync(temporary);
            MediaEncodingProfile profile = composition.CreateDefaultEncodingProfile();
            if (original.EmbeddedAudioTracks.Count == 0) profile.Audio = null;
            TranscodeFailureReason result = await composition.RenderToFileAsync(target, MediaTrimmingPreference.Precise, profile)
                .AsTask(cancellationToken, progress);
            if (result != TranscodeFailureReason.None)
                throw new InvalidOperationException(Loc.Format("EditorExportFailed", result));
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
