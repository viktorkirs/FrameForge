namespace FrameForge.Core;

public enum VideoEditClipKind { Media, Image, Text }
public enum VideoTextAlignment { Left, Center, Right }

/// <summary>Editable text; size is in pixels at ReferenceWidth and scales with the output canvas.</summary>
public sealed record VideoEditText
{
    public string Content { get; init; } = "";
    public string FontFamily { get; init; } = "Segoe UI";
    public double FontSize { get; init; } = 48;
    public string Color { get; init; } = "#FFFFFFFF";
    public bool Bold { get; init; }
    public bool Italic { get; init; }
    public bool Underline { get; init; }
    public VideoTextAlignment Alignment { get; init; } = VideoTextAlignment.Center;
    public uint ReferenceWidth { get; init; } = 1920;
    public bool IsValid => Content is { Length: <= 4000 } && Content.Count(c => c == '\n') <= 100
        && FontFamily is { Length: > 0 and <= 128 } && FontFamily.IndexOfAny(['/', '\\', ':', '#']) < 0
        && double.IsFinite(FontSize) && FontSize is >= 8 and <= 512 && ReferenceWidth is >= 2 and <= 16384
        && Enum.IsDefined(Alignment) && Color is { Length: 9 } && Color[0] == '#'
        && Color.Skip(1).All(Uri.IsHexDigit);
}

public sealed partial class VideoEditTimeline
{
    public Guid? AddImageLayer(string path, TimeSpan duration, TimeSpan position, string name) =>
        string.IsNullOrWhiteSpace(path) ? null : AddStillLayer(VideoEditClipKind.Image, path, null, duration, position, name);

    public Guid? AddTextLayer(VideoEditText text, TimeSpan duration, TimeSpan position, string name) =>
        !text.IsValid ? null : AddStillLayer(VideoEditClipKind.Text, null, text, duration, position, name);

    Guid? AddStillLayer(VideoEditClipKind kind, string? path, VideoEditText? text, TimeSpan duration, TimeSpan position, string name)
    {
        if (duration < MinimumDuration || position < TimeSpan.Zero || position + duration > TimeSpan.FromDays(7)
            || string.IsNullOrWhiteSpace(name) || name.Length > 256 || _tracks.Count >= 128) return null;
        Remember();
        var clip = new VideoEditClip(Guid.NewGuid(), TimeSpan.Zero, duration)
        {
            Kind = kind, Text = text, SourcePath = path, OriginalDuration = TimeSpan.FromDays(7), Position = position,
            Transform = kind == VideoEditClipKind.Image ? new() { Scale = .5 } : new()
        };
        var track = new VideoEditTrack(Guid.NewGuid(), VideoEditTrackKind.Video, name, [clip]);
        _tracks.Add(track); _layerOrder.Insert(0, track.Id); SelectedTrackId = track.Id; SelectedIndex = 0;
        return track.Id;
    }

    public bool SetSelectedText(VideoEditText text)
    {
        if (SelectedClip is not { Kind: VideoEditClipKind.Text } clip || !text.IsValid || clip.Text == text) return false;
        Remember(); ReplaceSelected(SelectedIndex, clip with { Text = text }); return true;
    }
}
