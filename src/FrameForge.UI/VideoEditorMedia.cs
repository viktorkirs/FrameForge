using System.IO;
using System.Windows.Media.Imaging;
using FrameForge.Core;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Media.Effects;
using Windows.Foundation;
using Windows.Storage;

namespace FrameForge.UI;

public sealed record VideoEditorSource(TimeSpan Duration, uint Width, uint Height, double FrameRate, bool HasAudio);
internal readonly record struct VideoEditorFrameRequest(TimeSpan Position, TimeSpan Start, TimeSpan End);

/// <summary>A source decoder reused for paused frame requests, independent of the playback clock.</summary>
internal sealed class VideoEditorPreview(MediaComposition composition, VideoEditorSource source)
{
    readonly SemaphoreSlim _decodeGate = new(1, 1);
    public VideoEditorSource Source { get; } = source;

    public async Task<BitmapSource> ReadFrameAsync(VideoEditorFrameRequest request, CancellationToken cancellationToken)
    {
        await _decodeGate.WaitAsync(cancellationToken);
        try { return await ReadFrameCoreAsync(request, cancellationToken); }
        finally { _decodeGate.Release(); }
    }

    async Task<BitmapSource> ReadFrameCoreAsync(VideoEditorFrameRequest request, CancellationToken cancellationToken)
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
    public static async Task<TimeSpan> ReadAudioDurationAsync(string path)
    {
        var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path));
        return (await BackgroundAudioTrack.CreateFromFileAsync(file)).OriginalDuration;
    }

    public static async Task ExportAsync(VideoEditSnapshot snapshot, string destinationPath, uint width, uint height,
        double frameRate, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var intermediates = new List<string>();
        try { await ExportCompositionAsync(snapshot, destinationPath, width, height, frameRate, intermediates, progress, cancellationToken); }
        finally { foreach (string path in intermediates) if (File.Exists(path)) File.Delete(path); }
    }

    static async Task ExportCompositionAsync(VideoEditSnapshot snapshot, string destinationPath, uint width, uint height,
        double frameRate, List<string> intermediates, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        string destination = Path.GetFullPath(destinationPath);
        VideoEditClip[] all = snapshot.Clips.Concat(snapshot.Tracks.SelectMany(track => track.Clips)).ToArray();
        if (snapshot.SourcePath != null && string.Equals(Path.GetFullPath(snapshot.SourcePath), destination, StringComparison.OrdinalIgnoreCase) ||
            all.Where(clip => clip.Kind != VideoEditClipKind.Text).Any(clip => string.Equals(Path.GetFullPath(clip.SourcePath ?? snapshot.SourcePath!), destination, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException(Loc.Get("EditorProtectSource"));
        if (snapshot.Duration < VideoEditTimeline.MinimumDuration) throw new InvalidOperationException(Loc.Get("EditorEmpty"));
        cancellationToken.ThrowIfCancellationRequested();
        var composition = new MediaComposition();
        composition.Clips.Add(MediaClip.CreateFromColor(Windows.UI.Color.FromArgb(255, 0, 0, 0), snapshot.Duration));
        var originals = new Dictionary<string, MediaClip>(StringComparer.OrdinalIgnoreCase);
        bool hasAudio = false;
        VideoEditClip[] videos = snapshot.Clips.Concat(snapshot.Tracks.Where(track => track.Kind == VideoEditTrackKind.Video).SelectMany(track => track.Clips)).ToArray();
        static bool NeedsTransform(VideoEditClip clip) => clip.Transform.Left != 0 || clip.Transform.Top != 0 || clip.Transform.Right != 0 || clip.Transform.Bottom != 0 || clip.Transform.QuarterTurns != 0;
        int transformCount = videos.Count(clip => !clip.IsStill && NeedsTransform(clip)), transformed = 0;

        async Task AddVideo(VideoEditClip interval, TimeSpan position, MediaOverlayLayer layer, bool muted)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (interval.IsStill)
            {
                ValidateInterval(interval, interval.OriginalDuration);
                BitmapSource bitmap = interval.Kind == VideoEditClipKind.Text
                    ? VideoEditorVisuals.RenderText(interval.Text!) : VideoEditorVisuals.ReadImage(interval.SourcePath!);
                BitmapSource transformedBitmap = VideoEditorVisuals.Transform(bitmap, interval.Transform);
                string temporaryImage = Path.Combine(Path.GetDirectoryName(destination)!, $".frameforge-layer-{Guid.NewGuid():N}.png");
                intermediates.Add(temporaryImage);
                VideoEditorVisuals.SaveOverlayImage(transformedBitmap, temporaryImage);
                MediaClip imageClip = await MediaClip.CreateFromImageFileAsync(await StorageFile.GetFileFromPathAsync(temporaryImage), interval.Duration).AsTask(cancellationToken);
                double fit = VideoEditorVisuals.Fit(interval, width, height, transformedBitmap.PixelWidth, transformedBitmap.PixelHeight) * interval.Transform.Scale;
                double imageWidth = transformedBitmap.PixelWidth * fit, imageHeight = transformedBitmap.PixelHeight * fit;
                AddOverlays(interval, imageClip, new Rect((width - imageWidth) / 2 + interval.Transform.OffsetX * width,
                    (height - imageHeight) / 2 + interval.Transform.OffsetY * height, imageWidth, imageHeight), position, layer);
                return;
            }
            string path = Path.GetFullPath(interval.SourcePath ?? snapshot.SourcePath!);
            if (!originals.TryGetValue(path, out MediaClip? original))
            {
                original = await MediaClip.CreateFromFileAsync(await StorageFile.GetFileFromPathAsync(path));
                originals.Add(path, original);
            }
            ValidateInterval(interval, original.OriginalDuration);
            MediaClip clip = original.Clone();
            clip.TrimTimeFromStart = interval.Start;
            clip.TrimTimeFromEnd = original.OriginalDuration - interval.End;
            clip.Volume = muted ? 0 : interval.Volume;
            VideoEncodingProperties video = original.GetVideoEncodingProperties();
            VideoFrameTransform transform = interval.Transform;
            double cropWidth = Math.Max(2, video.Width * (1 - transform.Left - transform.Right));
            double cropHeight = Math.Max(2, video.Height * (1 - transform.Top - transform.Bottom));
            bool rotated = transform.QuarterTurns % 2 == 1;
            double rotatedWidth = rotated ? cropHeight : cropWidth, rotatedHeight = rotated ? cropWidth : cropHeight;
            if (NeedsTransform(interval))
            {
                var effect = new VideoTransformEffectDefinition
                {
                    // The Windows transform applies rotation before cropping, so crop coordinates
                    // must be expressed in the rotated source, rather than the original frame.
                    CropRectangle = transform.QuarterTurns switch
                    {
                        1 => new Rect(video.Height * transform.Bottom, video.Width * transform.Left, cropHeight, cropWidth),
                        2 => new Rect(video.Width * transform.Right, video.Height * transform.Bottom, cropWidth, cropHeight),
                        3 => new Rect(video.Height * transform.Top, video.Width * transform.Right, cropHeight, cropWidth),
                        _ => new Rect(video.Width * transform.Left, video.Height * transform.Top, cropWidth, cropHeight)
                    },
                    Rotation = (MediaRotation)transform.QuarterTurns,
                    OutputSize = new Size(Even(rotatedWidth), Even(rotatedHeight)),
                    PaddingColor = Windows.UI.Color.FromArgb(255, 0, 0, 0)
                };
                string intermediate = Path.Combine(Path.GetDirectoryName(destination)!, $".frameforge-transform-{Guid.NewGuid():N}.mp4");
                intermediates.Add(intermediate);
                using (File.Create(intermediate)) { }
                var target = await StorageFile.GetFileFromPathAsync(intermediate);
                var transformedProfile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD720p);
                transformedProfile.Video.Width = (uint)Even(rotatedWidth);
                transformedProfile.Video.Height = (uint)Even(rotatedHeight);
                transformedProfile.Video.FrameRate.Numerator = video.FrameRate.Numerator;
                transformedProfile.Video.FrameRate.Denominator = video.FrameRate.Denominator;
                transformedProfile.Audio = null; // Mix original audio once, without intermediate AAC generations.
                var transcoder = new MediaTranscoder { TrimStartTime = interval.Start, TrimStopTime = interval.End };
                transcoder.AddVideoEffect(effect.ActivatableClassId, true, effect.Properties);
                PrepareTranscodeResult prepared = await transcoder.PrepareFileTranscodeAsync(await StorageFile.GetFileFromPathAsync(path), target, transformedProfile).AsTask(cancellationToken);
                if (!prepared.CanTranscode) throw new InvalidOperationException(Loc.Format("EditorExportFailed", prepared.FailureReason));
                int ordinal = transformed;
                var transformProgress = new Progress<double>(value => progress?.Report((ordinal + value / 100) / transformCount * 40));
                await prepared.TranscodeAsync().AsTask(cancellationToken, transformProgress);
                transformed++;
                clip = await MediaClip.CreateFromFileAsync(target);
                if (clip.OriginalDuration > interval.Duration) clip.TrimTimeFromEnd = clip.OriginalDuration - interval.Duration;
                clip.Volume = muted ? 0 : interval.Volume;
            }
            double scale = Math.Min(width / rotatedWidth, height / rotatedHeight) * transform.Scale;
            double w = rotatedWidth * scale, h = rotatedHeight * scale;
            var rectangle = new Rect((width - w) / 2 + transform.OffsetX * width, (height - h) / 2 + transform.OffsetY * height, w, h);
            AddOverlays(interval, clip, rectangle, position, layer);
            if (!muted && interval.Volume > 0 && original.EmbeddedAudioTracks.Count > 0)
            {
                BackgroundAudioTrack audio = BackgroundAudioTrack.CreateFromEmbeddedAudioTrack(original.EmbeddedAudioTracks[0]);
                if (interval.Start < audio.OriginalDuration)
                {
                    audio.TrimTimeFromStart = interval.Start;
                    audio.TrimTimeFromEnd = TimeSpan.FromTicks(Math.Max(0, (audio.OriginalDuration - interval.End).Ticks));
                    audio.Delay = position; audio.Volume = interval.Volume;
                    composition.BackgroundAudioTracks.Add(audio); hasAudio = true;
                }
            }
        }

        void AddOverlays(VideoEditClip interval, MediaClip clip, Rect rectangle, TimeSpan position, MediaOverlayLayer layer)
        {
            if (interval.FadeIn == TimeSpan.Zero && interval.FadeOut == TimeSpan.Zero)
                layer.Overlays.Add(new MediaOverlay(clip, rectangle, 1) { Delay = position, AudioEnabled = false });
            else
            {
                // Native overlays keep their source interval and opacity independently. Splitting
                // only the fade region gives frame-stepped alpha without an external codec/runtime.
                long duration = Math.Min(interval.Duration.Ticks, clip.TrimmedDuration.Ticks);
                long step = Math.Max(1, (long)Math.Round(TimeSpan.TicksPerSecond / Math.Clamp(frameRate, 1, 60)));
                var cuts = new SortedSet<long> { 0, duration };
                for (long tick = 0; tick < Math.Min(duration, interval.FadeIn.Ticks); tick += step) cuts.Add(tick);
                cuts.Add(Math.Min(duration, interval.FadeIn.Ticks));
                for (long tick = Math.Max(0, duration - interval.FadeOut.Ticks); tick < duration; tick += step) cuts.Add(tick);
                long[] boundaries = cuts.ToArray();
                for (int i = 0; i < boundaries.Length - 1; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    long from = boundaries[i], to = boundaries[i + 1];
                    if (to <= from) continue;
                    var part = clip.Clone();
                    part.TrimTimeFromStart = clip.TrimTimeFromStart + TimeSpan.FromTicks(from);
                    part.TrimTimeFromEnd = clip.TrimTimeFromEnd + TimeSpan.FromTicks(clip.TrimmedDuration.Ticks - to);
                    layer.Overlays.Add(new MediaOverlay(part, rectangle, interval.OpacityAt(TimeSpan.FromTicks(from)))
                    { Delay = position + TimeSpan.FromTicks(from), AudioEnabled = false });
                }
            }
        }

        foreach (Guid id in snapshot.OrderedTrackIds.Reverse())
        {
            if (id == Guid.Empty)
            {
                var main = new MediaOverlayLayer();
                foreach (VideoEditClip clip in snapshot.Clips) await AddVideo(clip, clip.Position, main, snapshot.MainMuted);
                if (main.Overlays.Count > 0) composition.OverlayLayers.Add(main);
                continue;
            }
            VideoEditTrack track = snapshot.Tracks.First(track => track.Id == id);
            if (track.Kind == VideoEditTrackKind.Video)
            {
                var layer = new MediaOverlayLayer();
                foreach (VideoEditClip clip in track.Clips) await AddVideo(clip, clip.Position, layer, track.Muted);
                if (layer.Overlays.Count > 0) composition.OverlayLayers.Add(layer);
            }
            else foreach (VideoEditClip interval in track.Clips)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(interval.SourcePath!));
                BackgroundAudioTrack audio = await BackgroundAudioTrack.CreateFromFileAsync(file);
                ValidateInterval(interval, audio.OriginalDuration);
                audio.TrimTimeFromStart = interval.Start;
                audio.TrimTimeFromEnd = audio.OriginalDuration - interval.End;
                audio.Delay = interval.Position;
                audio.Volume = track.Muted ? 0 : interval.Volume;
                composition.BackgroundAudioTracks.Add(audio);
                hasAudio |= audio.Volume > 0;
            }
        }
        var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD720p);
        profile.Video.Width = (uint)Even(width);
        profile.Video.Height = (uint)Even(height);
        profile.Video.FrameRate.Numerator = (uint)Math.Round(Math.Clamp(frameRate, 1, 120) * 1000);
        profile.Video.FrameRate.Denominator = 1000;
        profile.Video.Bitrate = (uint)Math.Clamp(width * (double)height * frameRate * .16, 1_000_000, 60_000_000);
        if (!hasAudio) profile.Audio = null;
        IProgress<double>? renderProgress = transformCount == 0 ? progress : new Progress<double>(value => progress?.Report(40 + value * .6));
        await RenderAsync(composition, profile, destination, renderProgress, cancellationToken);
    }

    static int Even(double value) => Math.Max(2, (int)Math.Round(value / 2) * 2);

    static void ValidateInterval(VideoEditClip interval, TimeSpan duration)
    {
        if (!interval.HasValidVisual || interval.Start < TimeSpan.Zero || interval.End > duration || interval.Duration < VideoEditTimeline.MinimumDuration ||
            interval.Position < TimeSpan.Zero || !interval.Transform.IsValid || !double.IsFinite(interval.Volume) || interval.Volume is < 0 or > 1 ||
            interval.FadeIn < TimeSpan.Zero || interval.FadeOut < TimeSpan.Zero || interval.FadeIn + interval.FadeOut > interval.Duration)
            throw new ArgumentOutOfRangeException(nameof(interval));
    }

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
        if (clips.Count > 1 && clips.All(clip => clip.Position == TimeSpan.Zero))
        {
            TimeSpan next = TimeSpan.Zero;
            clips = clips.Select(clip => { VideoEditClip positioned = clip with { Position = next }; next += clip.Duration; return positioned; }).ToArray();
        }
        TimeSpan expected = TimeSpan.Zero;
        bool hasGaps = false;
        foreach (VideoEditClip clip in clips) { hasGaps |= clip.Position != expected; expected = clip.Position + clip.Duration; }
        if (hasGaps || clips.Any(clip => clip.Transform != new VideoFrameTransform() || clip.Volume != 1 || clip.FadeIn != TimeSpan.Zero || clip.FadeOut != TimeSpan.Zero ||
            clip.SourcePath != null && !string.Equals(Path.GetFullPath(clip.SourcePath), Path.GetFullPath(sourcePath), StringComparison.OrdinalIgnoreCase)))
        {
            VideoEditorSource info = await ReadAsync(sourcePath);
            await ExportAsync(new(Path.GetFullPath(sourcePath), info.Duration, clips, Array.Empty<VideoEditTrack>(), false), destinationPath,
                info.Width, info.Height, info.FrameRate, progress, cancellationToken);
            return;
        }
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
        MediaEncodingProfile profile = composition.CreateDefaultEncodingProfile();
        if (original.EmbeddedAudioTracks.Count == 0) profile.Audio = null;
        await RenderAsync(composition, profile, destination, progress, cancellationToken);
    }

    static async Task RenderAsync(MediaComposition composition, MediaEncodingProfile profile, string destination,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        // Render beside the destination, then replace it only after a successful, complete export.
        string temporary = Path.Combine(Path.GetDirectoryName(destination)!, $".frameforge-{Guid.NewGuid():N}.mp4");
        try
        {
            using (File.Create(temporary)) { }
            var target = await StorageFile.GetFileFromPathAsync(temporary);
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
