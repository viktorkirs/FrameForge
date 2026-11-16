using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using FrameForge.Core;
using FrameForge.Diagnostics;

namespace FrameForge.UI;

public partial class VideoEditorWindow
{
    const string ClipboardFormat = "FrameForge.EditorCopy.v1";
    static VideoEditCopy? _editorClipboard;
    static readonly JsonSerializerOptions ClipboardJson = new() { IgnoreReadOnlyProperties = true };

    void InitializeTimelineTools()
    {
        TimelineCanvas.RangeChanged += RefreshRangeTools;
        TimelineCanvas.EditRejected += () => StatusText.Text = Loc.Get("EditorOverlap");
    }

    void Timeline_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        ScrollTimeline(e.Delta, Keyboard.Modifiers.HasFlag(ModifierKeys.Control) || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
        e.Handled = true;
    }

    void ScrollTimeline(int delta, bool horizontal)
    {
        if (horizontal)
            TimelineScroll.ScrollToHorizontalOffset(TimelineScroll.HorizontalOffset - delta / 120d * 90);
        else TimelineVertical.ScrollToVerticalOffset(TimelineVertical.VerticalOffset - delta / 120d * 48);
    }

    void ScrollLeft_Click(object sender, RoutedEventArgs e) => TimelineScroll.ScrollToHorizontalOffset(TimelineScroll.HorizontalOffset - TimelineScroll.ViewportWidth * .7);
    void ScrollRight_Click(object sender, RoutedEventArgs e) => TimelineScroll.ScrollToHorizontalOffset(TimelineScroll.HorizontalOffset + TimelineScroll.ViewportWidth * .7);
    void FitTimeline_Click(object sender, RoutedEventArgs e)
    {
        TimelineCanvas.FitToContent(); ZoomSlider.Value = 1; UpdateExtent(); TimelineScroll.ScrollToHorizontalOffset(0);
    }

    void TrackHeight_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TimelineCanvas == null || TrackHeaders == null) return;
        TimelineCanvas.TrackHeightScale = e.NewValue;
        RefreshTracks(); UpdateExtent();
    }

    void RangeMode_Click(object sender, RoutedEventArgs e)
    {
        Pause(); TimelineCanvas.RangeSelectionMode = !TimelineCanvas.RangeSelectionMode;
        RangeModeButton.Background = (Brush)FindResource(TimelineCanvas.RangeSelectionMode ? "AccentBrush" : "CardBrush");
        if (!TimelineCanvas.RangeSelectionMode) TimelineCanvas.ClearRange();
        StatusText.Text = Loc.Get("EditorRangeHint");
    }

    void RefreshRangeTools()
    {
        if (CutRangeButton == null) return;
        CutRangeButton.IsEnabled = !_loading && !_exporting && _voiceRecorder == null && TimelineCanvas.HasRange;
        RangeLabel.Text = TimelineCanvas.HasRange
            ? $"{VideoTimelineControl.Format(TimelineCanvas.RangeStart!.Value, true)} → {VideoTimelineControl.Format(TimelineCanvas.RangeEnd!.Value, true)}" : "";
        CopyButton.IsEnabled = !_loading && !_exporting && _voiceRecorder == null && (_timeline?.SelectedClip != null || TimelineCanvas.HasRange);
    }

    bool CopyToClipboard(VideoEditCopy? copy)
    {
        if (copy?.IsValid != true) return false;
        _editorClipboard = copy;
        try { Clipboard.SetData(ClipboardFormat, JsonSerializer.Serialize(copy, ClipboardJson)); }
        catch (Exception ex) { Log.Error("Writing editor clipboard; keeping the local copy.", ex); }
        StatusText.Text = Loc.Get(copy.WholeTracks ? "EditorTrackCopied" : "EditorCopied");
        return true;
    }

    VideoEditCopy? ReadEditorClipboard()
    {
        try
        {
            if (Clipboard.GetData(ClipboardFormat) is string json && json.Length <= 16 * 1024 * 1024)
            {
                var copy = JsonSerializer.Deserialize<VideoEditCopy>(json, ClipboardJson);
                return copy?.IsValid == true ? copy : null;
            }
            return null;
        }
        catch (Exception ex) { Log.Error("Reading editor clipboard.", ex); return _editorClipboard; }
    }

    void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_timeline == null) return;
        CopyToClipboard(TimelineCanvas.HasRange ? _timeline.CopyRange(TimelineCanvas.RangeStart!.Value, TimelineCanvas.RangeEnd!.Value) : _timeline.CopySelection());
    }

    void CopyTrack_Click(object sender, RoutedEventArgs e) => CopyToClipboard(_timeline?.CopySelection(wholeTrack: true));
    async void Paste_Click(object sender, RoutedEventArgs e) => await PasteAsync(forceNewTracks: false);
    async void PasteTrack_Click(object sender, RoutedEventArgs e) => await PasteAsync(forceNewTracks: true);

    async Task PasteAsync(bool forceNewTracks)
    {
        if (_timeline == null || _loading || _exporting || _voiceRecorder != null) return;
        VideoEditCopy? copy = ReadEditorClipboard();
        if (copy?.IsValid != true) { StatusText.Text = Loc.Get("EditorClipboardEmpty"); return; }
        Pause(); _loading = true; RefreshControls();
        try
        {
            var staged = new Dictionary<string, VideoEditorPreview>(StringComparer.OrdinalIgnoreCase);
            var durations = new Dictionary<string, TimeSpan>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in copy.Tracks.SelectMany(track => track.Clips).Select(clip => Path.GetFullPath(clip.SourcePath!)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                bool video = copy.Tracks.Any(track => track.Kind == VideoEditTrackKind.Video && track.Clips.Any(clip => string.Equals(Path.GetFullPath(clip.SourcePath!), path, StringComparison.OrdinalIgnoreCase)));
                if (video)
                {
                    VideoEditorPreview preview = _previews.TryGetValue(path, out VideoEditorPreview? known) ? known : await VideoEditorMedia.OpenPreviewAsync(path);
                    staged[path] = preview; durations[path] = preview.Source.Duration;
                }
                else durations[path] = await VideoEditorMedia.ReadAudioDurationAsync(path);
                if (_closed) return;
            }
            foreach (VideoEditClip clip in copy.Tracks.SelectMany(track => track.Clips))
                if (clip.End > durations[Path.GetFullPath(clip.SourcePath!)]) throw new InvalidDataException(Loc.Format("EditorMediaChanged", clip.SourcePath));
            foreach (var source in staged) _previews[source.Key] = source.Value;
            copy = copy with { Tracks = copy.Tracks.Select(track => track with { Clips = track.Clips.Select(clip => clip with
            { SourcePath = Path.GetFullPath(clip.SourcePath!), OriginalDuration = durations[Path.GetFullPath(clip.SourcePath!)] }).ToArray() }).ToArray() };
            if (_timeline.Paste(copy, _position, forceNewTracks))
            {
                TimelineCanvas.ClearRange(); AfterEdit(selectStart: true);
                foreach (var source in durations) _ = ReadAdditionalWaveformAsync(source.Key, source.Value);
                StatusText.Text = Loc.Get("EditorPasted");
            }
        }
        catch (Exception ex) { Log.Error("Pasting editor clips.", ex); StatusText.Text = ex.Message; }
        finally { _loading = false; if (!_closed) RefreshControls(); }
    }

    void DeleteRange(bool copy)
    {
        if (_timeline == null || !TimelineCanvas.HasRange) return;
        Pause();
        TimeSpan start = TimelineCanvas.RangeStart!.Value, end = TimelineCanvas.RangeEnd!.Value;
        if (copy) CopyToClipboard(_timeline.CopyRange(start, end));
        if (_timeline.RemoveRange(start, end)) { TimelineCanvas.ClearRange(); AfterEdit(previewPosition: start); }
    }

    void CutRange_Click(object sender, RoutedEventArgs e) => DeleteRange(copy: true);
    void Cut_Click(object sender, RoutedEventArgs e)
    {
        if (TimelineCanvas.HasRange) DeleteRange(copy: true);
        else if (CopyToClipboard(_timeline?.CopySelection()) && _timeline?.DeleteSelected() == true) AfterEdit();
    }
}
