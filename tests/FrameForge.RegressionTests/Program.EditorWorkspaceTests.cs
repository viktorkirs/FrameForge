using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using FrameForge.Core;
using FrameForge.Encoding;
using FrameForge.UI;

internal static partial class Program
{
    static void RunEditorWorkspaceCoreTests()
    {
        Test("moving and trimming on the main track keeps all neighbors fixed", () =>
        {
            var timeline = new VideoEditTimeline(TimeSpan.FromSeconds(8), "source.mp4");
            timeline.Split(TimeSpan.FromSeconds(2)); timeline.Split(TimeSpan.FromSeconds(4));
            VideoEditClip last = timeline.Clips[2];
            Check(timeline.Trim(0, TimeSpan.FromSeconds(.5), TimeSpan.FromSeconds(1.5)), "Two-sided trim failed");
            Check(timeline.Clips[0].Position.TotalSeconds == .5 && timeline.Clips[0].Position + timeline.Clips[0].Duration == TimeSpan.FromSeconds(1.5), "Left and right handles moved the wrong boundaries");
            Check(timeline.Clips[1].Position.TotalSeconds == 2 && timeline.Clips[2] == last, "Trimming changed another clip");
            Check(timeline.SetSelectedPosition(TimeSpan.FromSeconds(9)), "Could not move into empty space after the project");
            Check(timeline.Clips.Single(clip => clip.Id == last.Id) == last && timeline.Resolve(TimeSpan.FromSeconds(8.5)) == null, "Moving changed a neighbor or filled the gap with old frames");
            Check(!timeline.SetSelectedPosition(TimeSpan.FromSeconds(3)), "Movement overwrote an occupied slot");
            Check(timeline.Undo() && timeline.Clips[0].Position.TotalSeconds == .5, "Undo lost the independent placement");
        });
        Test("cutting a range affects every track without closing gaps and is one undoable edit", () =>
        {
            var timeline = new VideoEditTimeline(TimeSpan.FromSeconds(8), "source.mp4");
            timeline.AddTrack(VideoEditTrackKind.Audio, "Voice");
            timeline.AddSource("voice.wav", TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(1), VideoEditTrackKind.Audio);
            timeline.AddTrack(VideoEditTrackKind.Video, "Overlay");
            timeline.AddSource("overlay.mp4", TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(6), VideoEditTrackKind.Video);
            VideoEditClip overlay = timeline.Tracks[1].Clips[0];
            VideoEditSnapshot before = timeline.Snapshot();
            VideoEditCopy copy = timeline.CopyRange(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4))!;
            Check(copy.IsValid && copy.Tracks.Count == 2 && copy.Tracks.All(track => track.Clips[0].Position == TimeSpan.Zero && track.Clips[0].Duration.TotalSeconds == 2), "Range copy did not intersect each lane at the same times");
            Check(timeline.RemoveRange(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)), "Range removal failed");
            Check(timeline.Clips.Count == 2 && timeline.Clips[1] is { Position.TotalSeconds: 4, Start.TotalSeconds: 4 }, "Main cut shifted or lost retained source frames");
            Check(timeline.Tracks[0].Clips.Count == 2 && timeline.Tracks[0].Clips[1] is { Position.TotalSeconds: 4, Start.TotalSeconds: 3 }, "Audio cut has a different time boundary");
            Check(timeline.Tracks[1].Clips.Single() == overlay && timeline.Duration.TotalSeconds == 8 && timeline.Resolve(TimeSpan.FromSeconds(3)) == null, "Cut moved later material or closed the gap");
            Check(timeline.Paste(copy, TimeSpan.FromSeconds(2)), "Could not paste the cut range back");
            Check(timeline.Resolve(TimeSpan.FromSeconds(3)) is { SourceTime.TotalSeconds: 3 }, "Range paste lost original source alignment");
            Check(timeline.Undo() && timeline.Undo() && timeline.Clips.SequenceEqual(before.Clips) && timeline.Tracks[0].Clips.SequenceEqual(before.Tracks[0].Clips), "Range operations cannot undo to the original edit");
        });
        Test("clip and whole-track copies paste independently and keep mute and transforms", () =>
        {
            var timeline = new VideoEditTimeline(TimeSpan.FromSeconds(2), "source.mp4");
            timeline.SetSelectedTransform(new() { Scale = .6, OffsetX = .2 }); timeline.SetSelectedVolume(.35);
            VideoEditCopy clip = timeline.CopySelection()!;
            Check(timeline.Paste(clip, TimeSpan.FromSeconds(4)) && timeline.Clips.Count == 2 && timeline.Clips[0].Position == TimeSpan.Zero, "Clip paste moved an existing clip");
            Check(timeline.SelectedClip is { Position.TotalSeconds: 4, Volume: .35, Transform.Scale: .6 }, "Clip paste lost its settings");
            Guid pastedId = timeline.SelectedClip!.Id;
            Check(pastedId != clip.Tracks[0].Clips[0].Id, "Pasted clips share edit identities");
            timeline.ToggleMute(null);
            VideoEditCopy whole = timeline.CopySelection(wholeTrack: true)!;
            Check(timeline.Paste(whole, TimeSpan.FromSeconds(100)) && timeline.Tracks.Count == 1 && timeline.Tracks[0].Muted, "Whole track paste did not create a separate muted lane");
            Check(timeline.Tracks[0].Clips.Select(item => item.Position).SequenceEqual(timeline.Clips.Select(item => item.Position)), "Whole track copy changed the relative placement");
            Check(timeline.Tracks[0].Clips.All(item => !timeline.Clips.Any(original => original.Id == item.Id)), "Copied tracks still share clip identities");
            VideoEditCopy invalid = clip with { Tracks = [clip.Tracks[0] with { Clips = [clip.Tracks[0].Clips[0], clip.Tracks[0].Clips[0]] }] };
            Check(!invalid.IsValid && !timeline.Paste(invalid, TimeSpan.Zero), "Clipboard allowed overlapping data to corrupt a track");
        });
        Test("version-one projects migrate to explicit placements and version-two gaps round-trip", () =>
        {
            string path = Path.Combine(_output, "legacy-v1.ffproj");
            var timeline = new VideoEditTimeline(TimeSpan.FromSeconds(6), Path.Combine(_output, "source.mp4"));
            timeline.Split(TimeSpan.FromSeconds(2)); timeline.Split(TimeSpan.FromSeconds(4));
            var legacy = new VideoEditProject(timeline.Snapshot() with { Clips = timeline.Clips.Select(clip => clip with { Position = TimeSpan.Zero }).ToArray() }, 320, 180, 30) { Version = 1 };
            File.WriteAllText(path, JsonSerializer.Serialize(legacy, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, IgnoreReadOnlyProperties = true }));
            VideoEditProject loaded = VideoEditProjectFile.Load(path);
            Check(loaded.Version == 2 && loaded.Timeline.Clips.Select(clip => clip.Position.TotalSeconds).SequenceEqual(new[] { 0d, 2d, 4d }), "Old projects no longer open at their original times");
            var restored = VideoEditTimeline.FromSnapshot(loaded.Timeline); restored.SelectedIndex = 1; restored.SetSelectedPosition(TimeSpan.FromSeconds(7));
            VideoEditProjectFile.Save(path, loaded with { Timeline = restored.Snapshot(), TrackHeightScale = .55, Zoom = .5 });
            VideoEditProject again = VideoEditProjectFile.Load(path);
            Check(again.Timeline.Clips.Any(clip => clip.Position.TotalSeconds == 7) && again.TrackHeightScale == .55 && again.Zoom == .5, "New projects lost gaps, compact tracks or timeline zoom");
        });
    }

    static void RunEditorWorkspaceUiTests(string source)
    {
        Test("black timeline intervals export as black video with silence and retain later clips", () =>
        {
            var timeline = new VideoEditTimeline(TimeSpan.FromSeconds(6), source);
            timeline.Split(TimeSpan.FromSeconds(1)); timeline.Split(TimeSpan.FromSeconds(2));
            timeline.SelectedIndex = 1; timeline.DeleteSelected();
            timeline.Trim(1, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3));
            string destination = Path.Combine(_output, "editor-independent-gaps.mp4");
            Wait(VideoEditorMedia.ExportAsync(timeline.Snapshot(), destination, 320, 180, 30));
            Mp4Probe first = Mp4Probe.Read(destination, 15, maxSamples: 15), gap = Mp4Probe.Read(destination, 45, maxSamples: 45), last = Mp4Probe.Read(destination, 75, maxSamples: 75);
            CheckColor(first, .5, .5, 2, "The first clip moved"); CheckColor(last, .5, .5, 1, "The later clip moved or lost source mapping");
            int pixel = (gap.PreviewHeight / 2 * gap.PreviewWidth + gap.PreviewWidth / 2) * 4;
            Check(gap.PreviewPixels!.AsSpan(pixel, 3).ToArray().All(channel => channel < 25), "Gap contains the previous frame instead of black");
            AudioProbe audio = AudioProbe.Read(destination);
            Check(audio.PreviewMono.Skip(57600).Take(19200).All(sample => Math.Abs(sample) < .005), "Original audio continued through the empty interval");
        });
        Test("timeline wheels, compact rows, splitters and range controls work together", () =>
        {
            var editor = new VideoEditorWindow(source); editor.Show();
            BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            VideoEditTimeline? timeline = null;
            try
            {
                for (int i = 0; i < 100 && typeof(VideoEditorWindow).GetField("_mediaReady", flags)!.GetValue(editor) is false; i++) Wait(Task.Delay(50));
                timeline = (VideoEditTimeline)typeof(VideoEditorWindow).GetField("_timeline", flags)!.GetValue(editor)!;
                for (int i = 0; i < 12; i++) timeline.AddTrack(i % 2 == 0 ? VideoEditTrackKind.Video : VideoEditTrackKind.Audio, "Track " + i);
                typeof(VideoEditorWindow).GetMethod("AfterEdit", flags)!.Invoke(editor, [false, null]);
                ((Slider)editor.FindName("ZoomSlider")).Value = 3;
                editor.UpdateLayout(); Wait(Task.Delay(100));
                var vertical = (ScrollViewer)editor.FindName("TimelineVertical"); var horizontal = (ScrollViewer)editor.FindName("TimelineScroll");
                typeof(VideoEditorWindow).GetMethod("ScrollTimeline", flags)!.Invoke(editor, [-120, false]); Wait(Task.Delay(30));
                Check(vertical.VerticalOffset > 0 && horizontal.HorizontalOffset == 0, "Ordinary wheel did not scroll the track list");
                double y = vertical.VerticalOffset;
                typeof(VideoEditorWindow).GetMethod("ScrollTimeline", flags)!.Invoke(editor, [-120, true]); Wait(Task.Delay(30));
                Check(horizontal.HorizontalOffset > 0 && vertical.VerticalOffset == y, "Ctrl+wheel did not scroll horizontally");
                var canvas = (VideoTimelineControl)editor.FindName("TimelineCanvas"); double height = canvas.Height;
                ((Slider)editor.FindName("TrackHeightSlider")).Value = .5;
                editor.UpdateLayout(); Wait(Task.Delay(30));
                Check(canvas.Height < height * .6 && ((Canvas)editor.FindName("TrackHeaders")).Height == canvas.Height, "Compact rows did not keep headers aligned");
                canvas.SetRange(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
                Check(((Button)editor.FindName("CutRangeButton")).IsEnabled, "Range selection did not enable the cut action");
                canvas.ClearRange(); Check(!((Button)editor.FindName("CutRangeButton")).IsEnabled, "Clearing a range left cut enabled");
                void Drag(GridSplitter splitter, double dx, double dy)
                {
                    splitter.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
                    splitter.RaiseEvent(new DragDeltaEventArgs(dx, dy) { RoutedEvent = Thumb.DragDeltaEvent });
                    splitter.RaiseEvent(new DragCompletedEventArgs(dx, dy, false) { RoutedEvent = Thumb.DragCompletedEvent });
                    editor.UpdateLayout();
                }
                double width = ((ColumnDefinition)editor.FindName("InspectorColumn")).ActualWidth;
                Drag((GridSplitter)editor.FindName("InspectorSplitter"), -35, 0);
                Check(((ColumnDefinition)editor.FindName("InspectorColumn")).ActualWidth > width + 20, "The tool-panel splitter did not resize its column");
                double previewHeight = ((RowDefinition)editor.FindName("PreviewRow")).ActualHeight;
                Drag((GridSplitter)editor.FindName("PreviewTimelineSplitter"), 0, -35);
                Check(((RowDefinition)editor.FindName("PreviewRow")).ActualHeight < previewHeight - 20, "The preview/timeline splitter did not resize its rows");
                SaveWindow(editor, "editor-compact-resizable-workspace.png");
            }
            finally { while (timeline?.Undo() == true) { } editor.Close(); }
        });
        Test("the save prompt uses themed translated actions and cancel keeps editing", () =>
        {
            Loc.Set(AppLanguage.Russian);
            var owner = new Window { Width = 700, Height = 420 }; owner.Show();
            Exception? failure = null;
            try
            {
                Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    var dialog = Application.Current.Windows.OfType<AppDialog>().Single();
                    try
                    {
                        Check(((Button)dialog.FindName("DiscardAction")).Content as string == "Не сохранять" && ((Button)dialog.FindName("CancelAction")).Content as string == Loc.Get("Cancel"), "Dialog used system-language Yes/No buttons");
                        SaveWindow(dialog, "editor-themed-save-dialog.png");
                    }
                    catch (Exception ex) { failure = ex; }
                    finally { ((Button)dialog.FindName("CancelAction")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
                }, DispatcherPriority.ApplicationIdle);
                Check(AppDialog.ConfirmSave(owner) == MessageBoxResult.Cancel, "Cancel was interpreted as discarding the project");
                if (failure != null) throw failure;
            }
            finally { owner.Close(); }
        });
        Test("switching recording and editing keeps the same unfinished project alive", () =>
        {
            AppSettings original = SettingsStore.Load();
            SettingsStore.Save(new AppSettings { Language = AppLanguage.Russian, CheckUpdatesOnStartup = false, OutputDirectory = _output });
            Window? previous = Application.Current.MainWindow;
            var owner = new Window { Width = 700, Height = 500 }; Application.Current.MainWindow = owner; owner.Show();
            var model = new MainViewModel();
            BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            VideoEditTimeline? timeline = null;
            try
            {
                typeof(MainViewModel).GetField("_lastVideoFile", flags)!.SetValue(model, source);
                model.EditVideoCommand.Execute(null);
                var editor = Application.Current.Windows.OfType<VideoEditorWindow>().Single();
                for (int i = 0; i < 100 && (typeof(VideoEditorWindow).GetField("_timeline", flags)!.GetValue(editor) == null ||
                    typeof(VideoEditorWindow).GetField("_loading", flags)!.GetValue(editor) is true); i++) Wait(Task.Delay(50));
                timeline = (VideoEditTimeline)typeof(VideoEditorWindow).GetField("_timeline", flags)!.GetValue(editor)!;
                timeline.SetSelectedTransform(new() { Scale = .7 });
                Guid state = timeline.StateId;
                ((Button)((System.Windows.Controls.Primitives.UniformGrid)editor.FindName("WorkspaceNavigation")).Children[0]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(!editor.IsVisible && owner.IsVisible && timeline.StateId == state, "Switching to recording closed or discarded the edit");
                model.EditVideoCommand.Execute(null);
                Check(editor.IsVisible && ReferenceEquals(editor, Application.Current.Windows.OfType<VideoEditorWindow>().Single()) && timeline.StateId == state,
                    "Returning to editing opened a new project");
                Check(((System.Windows.Controls.Primitives.UniformGrid)editor.FindName("WorkspaceNavigation")).Visibility == Visibility.Visible, "Editor lacks the matching workspace navigation");
                editor.UpdateLayout(); Wait(Task.Delay(100)); SaveWindow(editor, "editor-recording-navigation.png");
            }
            finally
            {
                while (timeline?.Undo() == true) { }
                Wait(model.CloseEditorAsync()); Wait(model.ShutdownAsync()); owner.Close(); Application.Current.MainWindow = previous;
                SettingsStore.Save(original);
            }
        });
    }
}
