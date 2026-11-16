using System.IO;
using System.Windows;
using System.Windows.Controls;
using FrameForge.Core;
using FrameForge.Diagnostics;
using Microsoft.Win32;

namespace FrameForge.UI;

public partial class VideoEditorWindow
{
    string? _projectPath;
    Guid? _savedProjectState;
    bool _projectNeedsSave;

    void RefreshProjectTitle()
    {
        Title = (_projectPath == null ? "" : Path.GetFileName(_projectPath) + " — ") + Loc.Get("EditorTitle") + (HasUnexportedChanges() ? " *" : "");
    }

    bool SaveProject(bool saveAs = false)
    {
        if (_timeline == null || _source == null || _loading || _exporting || _voiceRecorder != null) return false;
        Pause();
        string? path = saveAs ? null : _projectPath;
        if (path == null)
        {
            var dialog = new SaveFileDialog
            {
                Filter = "FrameForge project (*.ffproj)|*.ffproj", DefaultExt = VideoEditProjectFile.Extension, AddExtension = true,
                OverwritePrompt = true, Title = Loc.Get("EditorSaveProject"),
                InitialDirectory = Path.GetDirectoryName(_projectPath ?? _sourcePath),
                FileName = (Path.GetFileNameWithoutExtension(_projectPath ?? _sourcePath) ?? "Project") + VideoEditProjectFile.Extension
            };
            if (ModalBackdrop.ShowDialog(dialog, this) != true) return false;
            path = dialog.FileName;
        }
        return SaveProjectTo(path);
    }

    bool SaveProjectTo(string path)
    {
        if (_timeline == null || _source == null) return false;
        try
        {
            VideoEditProjectFile.Save(path, new VideoEditProject(_timeline.Snapshot(), _source.Width, _source.Height, _source.FrameRate)
            { Playhead = _position, Zoom = ZoomSlider.Value, TrackHeightScale = TimelineCanvas.TrackHeightScale });
            _projectPath = Path.GetFullPath(path); _savedProjectState = _timeline.StateId; _projectNeedsSave = false;
            StatusText.Text = Loc.Format("EditorProjectSaved", path);
            RefreshProjectTitle();
            return true;
        }
        catch (Exception ex) { Log.Error("Saving editor project.", ex); StatusText.Text = ex.Message; return false; }
    }

    async void OpenProject_Click(object sender, RoutedEventArgs e)
    {
        if (_loading || _exporting || _voiceRecorder != null) return;
        Pause();
        var dialog = new OpenFileDialog { Filter = "FrameForge project (*.ffproj)|*.ffproj", CheckFileExists = true, Title = Loc.Get("EditorOpenProject") };
        if (ModalBackdrop.ShowDialog(dialog, this) == true && ConfirmDiscard()) await LoadProjectAsync(dialog.FileName);
    }

    void SaveProject_Click(object sender, RoutedEventArgs e) => SaveProject();
    void SaveProjectAs_Click(object sender, RoutedEventArgs e) => SaveProject(saveAs: true);

    async Task LoadProjectAsync(string path)
    {
        if (_loading || _exporting) return;
        Pause(); _loading = true; RefreshControls(); StatusText.Text = Loc.Get("EditorProjectLoading");
        try
        {
            VideoEditProject project = VideoEditProjectFile.Load(path);
            var previews = new Dictionary<string, VideoEditorPreview>(StringComparer.OrdinalIgnoreCase);
            var durations = new Dictionary<string, TimeSpan>(StringComparer.OrdinalIgnoreCase);
            var replacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var images = new Dictionary<string, System.Windows.Media.Imaging.BitmapSource>(StringComparer.OrdinalIgnoreCase);
            var texts = new Dictionary<Guid, (VideoEditText Text, System.Windows.Media.Imaging.BitmapSource Bitmap)>();
            VideoEditSnapshot snapshot = project.Timeline;
            bool relinked = false;
            foreach (string source in VideoEditProjectFile.MediaPaths(snapshot))
            {
                string sourcePath = source;
                if (!File.Exists(sourcePath))
                {
                    var dialog = new OpenFileDialog
                    {
                        Title = Loc.Format("EditorLocateMedia", Path.GetFileName(source)), FileName = Path.GetFileName(source),
                        Filter = "Media|*.mp4;*.mov;*.m4v;*.wmv;*.avi;*.wav;*.mp3;*.flac;*.m4a;*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|All files|*.*", CheckFileExists = true
                    };
                    if (ModalBackdrop.ShowDialog(dialog, this) != true) throw new FileNotFoundException(Loc.Format("EditorMissingMedia", source));
                    sourcePath = Path.GetFullPath(dialog.FileName); relinked = true;
                }
                replacements[source] = sourcePath;
                bool image = snapshot.Tracks.SelectMany(track => track.Clips).Any(clip => clip.Kind == VideoEditClipKind.Image && clip.SourcePath == source);
                bool video = snapshot.Clips.Any(clip => (clip.SourcePath ?? snapshot.SourcePath) == source) ||
                    snapshot.Tracks.Any(track => track.Kind == VideoEditTrackKind.Video && track.Clips.Any(clip => clip.Kind == VideoEditClipKind.Media && clip.SourcePath == source));
                if (image)
                { images[sourcePath] = VideoEditorVisuals.ReadImage(sourcePath); durations[sourcePath] = TimeSpan.FromDays(7); }
                else if (video)
                {
                    VideoEditorPreview preview = await VideoEditorMedia.OpenPreviewAsync(sourcePath);
                    previews[sourcePath] = preview; durations[sourcePath] = preview.Source.Duration;
                }
                else durations[sourcePath] = await VideoEditorMedia.ReadAudioDurationAsync(sourcePath);
                if (_closed) return;
            }
            VideoEditClip MapClip(VideoEditClip clip)
            {
                if (clip.Kind == VideoEditClipKind.Text) { texts[clip.Id] = (clip.Text!, VideoEditorVisuals.RenderText(clip.Text!)); return clip; }
                string replacement = replacements[clip.SourcePath ?? snapshot.SourcePath!];
                if (clip.End > durations[replacement]) throw new InvalidDataException(Loc.Format("EditorMediaChanged", replacement));
                return clip with { SourcePath = replacement, OriginalDuration = durations[replacement] };
            }
            snapshot = snapshot with
            {
                SourcePath = snapshot.SourcePath != null && replacements.TryGetValue(snapshot.SourcePath, out string? replaced) ? replaced : snapshot.SourcePath,
                Clips = snapshot.Clips.Select(MapClip).ToArray(),
                Tracks = snapshot.Tracks.Select(track => track with { Clips = track.Clips.Select(MapClip).ToArray() }).ToArray()
            };
            // Stage and validate all media before replacing the current editing session.
            _waveformCancellation?.Cancel(); _waveformCancellation?.Dispose();
            _waveformCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            Player.Close(); CloseTrackPlayback(); _previews.Clear(); ClearVisualAssets();
            foreach (var entry in previews) _previews.Add(entry.Key, entry.Value);
            foreach (var entry in images) _imageBitmaps.Add(entry.Key, entry.Value);
            foreach (var entry in texts) _textBitmaps.Add(entry.Key, entry.Value);
            _timeline = VideoEditTimeline.FromSnapshot(snapshot);
            _source = new(snapshot.SourceDuration, project.Width, project.Height, project.FrameRate, false);
            _sourcePath = snapshot.SourcePath ?? previews.Keys.FirstOrDefault() ?? Path.GetFullPath(path);
            _framePreview = null; _playerPath = null; _mediaReady = false;
            _previewRevision++; _pendingPreviewFrame = null; PausedFrame.Source = null;
            PausedFrame.Visibility = Visibility.Collapsed;
            TimelineCanvas.Timeline = _timeline; TimelineCanvas.Waveform = null;
            TimelineCanvas.SourceThumbnails.Clear(); TimelineCanvas.Waveforms.Clear();
            _thumbnails = new(); _pendingThumbnails = new(); TimelineCanvas.Thumbnails = _thumbnails;
            _projectPath = Path.GetFullPath(path); _savedProjectState = _timeline.StateId; _projectNeedsSave = relinked;
            _frameMode = "move"; FinishCropButton.Visibility = Visibility.Collapsed;
            FileLabel.Text = $"{Path.GetFileName(path)}  ·  {project.Width} × {project.Height}  ·  {project.FrameRate:0.##} FPS";
            FileLabel.ToolTip = _projectPath;
            PreviewMessage.Visibility = Visibility.Collapsed;
            ZoomSlider.Value = project.Zoom;
            TrackHeightSlider.Value = project.TrackHeightScale;
            ConfigureOutputFormats();
            RefreshSelection(); RefreshTracks(); UpdateExtent();
            Seek(project.Playhead);
            foreach (var entry in durations.Where(entry => !images.ContainsKey(entry.Key))) _ = ReadAdditionalWaveformAsync(entry.Key, entry.Value);
            _ = ReadThumbnailsAsync();
            StatusText.Text = Loc.Get("EditorProjectOpened");
        }
        catch (Exception ex) { Log.Error("Opening editor project.", ex); if (!_closed) StatusText.Text = ex.Message; }
        finally { _loading = false; if (!_closed) RefreshControls(); }
    }
}
