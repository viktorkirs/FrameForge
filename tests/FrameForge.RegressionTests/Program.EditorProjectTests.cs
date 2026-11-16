using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using FrameForge.Core;
using FrameForge.Encoding;
using FrameForge.UI;

internal static partial class Program
{
    static void RunEditorProjectCoreTests()
    {
        Test("ffproj round-trip retains source paths, tracks, transforms, fades and output settings", () =>
        {
            string directory = Path.Combine(_output, "project-roundtrip"); Directory.CreateDirectory(directory);
            string source = Path.Combine(directory, "исходник.mp4"), voice = Path.Combine(directory, "voice.wav");
            var timeline = new VideoEditTimeline(TimeSpan.FromSeconds(8), source);
            timeline.SetSelectedTransform(new() { Left = .125, QuarterTurns = 3, Scale = 1.8, OffsetX = -.2 });
            timeline.SetSelectedFades(TimeSpan.FromSeconds(.75), TimeSpan.FromSeconds(.5));
            timeline.SetSelectedVolume(.4); timeline.Split(TimeSpan.FromSeconds(3));
            timeline.AddTrack(VideoEditTrackKind.Audio, "Озвучка");
            timeline.AddSource(voice, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1.25), VideoEditTrackKind.Audio);
            timeline.ToggleMute(timeline.SelectedTrackId);
            var project = new VideoEditProject(timeline.Snapshot(), 1080, 1920, 60) { Playhead = TimeSpan.FromSeconds(2.125), Zoom = 4 };
            string path = Path.Combine(directory, "Монтаж.ffproj"); VideoEditProjectFile.Save(path, project);
            Check(!File.ReadAllText(path).Contains(directory.Replace("\\", "\\\\"), StringComparison.OrdinalIgnoreCase), "Project stored absolute paths for neighboring media");
            VideoEditProject loaded = VideoEditProjectFile.Load(path);
            Check(loaded.Width == 1080 && loaded.Height == 1920 && loaded.FrameRate == 60 && loaded.Playhead == project.Playhead && loaded.Zoom == 4, "Project lost output or workspace settings");
            Check(loaded.Timeline.Clips.SequenceEqual(project.Timeline.Clips) && loaded.Timeline.Tracks[0].Clips.SequenceEqual(project.Timeline.Tracks[0].Clips)
                && loaded.Timeline.Tracks[0].Muted, "Project lost editable clip or track state");
            VideoEditTimeline restored = VideoEditTimeline.FromSnapshot(loaded.Timeline);
            Check(!restored.CanUndo && !restored.HasChanges && restored.Duration == timeline.Duration, "Restored project is dirty or has invalid history");
            restored.SetSelectedVolume(.2); Check(restored.Undo() && !restored.HasChanges, "Editing a restored project cannot undo to the saved state");
            string relocated = Path.Combine(_output, "project-relocated"); Directory.CreateDirectory(relocated);
            string copied = Path.Combine(relocated, "Монтаж.ffproj"); File.Copy(path, copied, overwrite: true);
            VideoEditProject moved = VideoEditProjectFile.Load(copied);
            Check(moved.Timeline.Clips[0].SourcePath == Path.Combine(relocated, "исходник.mp4"), "Relative media paths did not follow the relocated project folder");
        });
        Test("invalid ffproj data cannot overwrite an existing project", () =>
        {
            string path = Path.Combine(_output, "protected-project.ffproj");
            var project = new VideoEditProject(new VideoEditTimeline(TimeSpan.FromSeconds(3), Path.Combine(_output, "source.mp4")).Snapshot(), 320, 180, 30);
            VideoEditProjectFile.Save(path, project); byte[] before = File.ReadAllBytes(path);
            bool rejected = false;
            try { VideoEditProjectFile.Save(path, project with { Version = 999 }); } catch (InvalidDataException) { rejected = true; }
            Check(rejected && before.SequenceEqual(File.ReadAllBytes(path)), "Invalid project replaced the last saved version");
            File.WriteAllText(Path.Combine(_output, "broken.ffproj"), "{\"format\":\"FrameForgeProject\",\"version\":1,\"timeline\":null}");
            rejected = false;
            try { VideoEditProjectFile.Load(Path.Combine(_output, "broken.ffproj")); } catch (InvalidDataException) { rejected = true; }
            Check(rejected && !Directory.GetFiles(_output, ".frameforge-project-*.tmp").Any(), "Invalid input was accepted or save left temporary files");
        });
    }

    static void RunEditorProjectMediaTests(string source, string silent)
    {
        Test("frame alpha fades blend into the underlying video and vertical export has the selected dimensions", () =>
        {
            var timeline = new VideoEditTimeline(TimeSpan.FromSeconds(6), silent);
            timeline.Trim(0, TimeSpan.Zero, TimeSpan.FromSeconds(1));
            timeline.AddTrack(VideoEditTrackKind.Video, "Fading video");
            timeline.AddSource(silent, TimeSpan.FromSeconds(6), TimeSpan.Zero, VideoEditTrackKind.Video);
            timeline.Trim(0, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(5));
            timeline.SetSelectedPosition(TimeSpan.Zero);
            timeline.SetSelectedFades(TimeSpan.FromSeconds(.5), TimeSpan.FromSeconds(.25));
            string destination = Path.Combine(_output, "editor-fades.mp4");
            Wait(VideoEditorMedia.ExportAsync(timeline.Snapshot(), destination, 180, 320, 30));
            Mp4Probe first = Mp4Probe.Read(destination, 1, maxSamples: 1), middle = Mp4Probe.Read(destination, 18, maxSamples: 18), last = Mp4Probe.Read(destination, 30, maxSamples: 30);
            Check(first.Width == 180 && first.Height == 320, "Portrait output dimensions were ignored");
            CheckColor(first, .5, .5, 2, "A transparent new clip hid the underlying red video");
            CheckColor(middle, .5, .5, 0, "Fade did not reach the blue video at full opacity");
            CheckColor(last, .5, .5, 2, "Fade out did not reveal the underlying red video");
            timeline.SelectedTrackId = null; timeline.SelectedIndex = 0;
            timeline.SetSelectedFades(TimeSpan.FromSeconds(.5), TimeSpan.Zero);
            string starting = Path.Combine(_output, "editor-start-fade.mp4");
            Wait(VideoEditorMedia.ExportAsync(silent, timeline.Clips, starting));
            Mp4Probe start = Mp4Probe.Read(starting, 1, maxSamples: 1);
            int center = (start.PreviewHeight / 2 * start.PreviewWidth + start.PreviewWidth / 2) * 4;
            Check(start.PreviewPixels!.AsSpan(center, 3).ToArray().All(value => value < 25), "The first clip did not fade in from transparent black");
        });
        Test("ffproj opens an unfinished edit with all sources and can be edited and saved again", () =>
        {
            var timeline = new VideoEditTimeline(TimeSpan.FromSeconds(6), source);
            timeline.Trim(0, TimeSpan.FromSeconds(.5), TimeSpan.FromSeconds(2.5));
            timeline.SetSelectedFades(TimeSpan.FromSeconds(.4), TimeSpan.Zero);
            timeline.AddSource(silent, TimeSpan.FromSeconds(6), TimeSpan.Zero, VideoEditTrackKind.Video);
            timeline.Trim(1, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(5));
            string path = Path.Combine(_output, "resume.ffproj");
            VideoEditProjectFile.Save(path, new(timeline.Snapshot(), 1080, 1920, 30) { Playhead = TimeSpan.FromSeconds(.7), Zoom = 2 });
            var editor = new VideoEditorWindow(path); editor.Show();
            BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            try
            {
                for (int i = 0; i < 100 && (typeof(VideoEditorWindow).GetField("_timeline", flags)!.GetValue(editor) == null ||
                    typeof(VideoEditorWindow).GetField("_loading", flags)!.GetValue(editor) is true || typeof(VideoEditorWindow).GetField("_mediaReady", flags)!.GetValue(editor) is false); i++) Wait(Task.Delay(50));
                var restored = (VideoEditTimeline?)typeof(VideoEditorWindow).GetField("_timeline", flags)!.GetValue(editor);
                Check(restored?.Clips.Count == 2 && restored.Clips[0].FadeIn.TotalSeconds == .4, "Editor did not restore project clips and fades");
                var output = (VideoEditorSource)typeof(VideoEditorWindow).GetField("_source", flags)!.GetValue(editor)!;
                Check(output.Width == 1080 && output.Height == 1920 && ((Slider)editor.FindName("ZoomSlider")).Value == 2, "Editor did not restore the vertical canvas or zoom");
                Check(!editor.Title.EndsWith('*'), "Freshly loaded project has unsaved changes");
                restored!.SetSelectedVolume(.3);
                bool saved = (bool)typeof(VideoEditorWindow).GetMethod("SaveProjectTo", flags)!.Invoke(editor, [path])!;
                Check(saved && VideoEditProjectFile.Load(path).Timeline.Clips[0].Volume == .3 && !editor.Title.EndsWith('*'), "Resumed edits were not saved back to the project");
                editor.UpdateLayout(); Wait(Task.Delay(300));
                SaveWindow(editor, "editor-resumed-portrait-project.png");
            }
            finally { editor.Close(); }
        });
        Test("canceling transformed multitrack export preserves the destination and removes temporary files", () =>
        {
            var timeline = new VideoEditTimeline(TimeSpan.FromSeconds(6), source);
            timeline.Trim(0, TimeSpan.Zero, TimeSpan.FromSeconds(2));
            timeline.SetSelectedTransform(new() { Left = .2, QuarterTurns = 1 });
            timeline.SetSelectedFades(TimeSpan.FromSeconds(.3), TimeSpan.FromSeconds(.3));
            string destination = Path.Combine(_output, "editor-cancel-preserve.mp4");
            byte[] before = File.ReadAllBytes(silent); File.WriteAllBytes(destination, before);
            using var cancel = new CancellationTokenSource();
            bool canceled = false;
            try { Wait(VideoEditorMedia.ExportAsync(timeline.Snapshot(), destination, 320, 180, 30, new Progress<double>(_ => cancel.Cancel()), cancel.Token)); }
            catch (OperationCanceledException) { canceled = true; }
            Check(canceled && before.SequenceEqual(File.ReadAllBytes(destination)), "Canceled project export replaced an existing destination");
            Check(!Directory.GetFiles(_output, ".frameforge-*.mp4").Any(), "Canceled transform left intermediate files");
        });
    }
}
