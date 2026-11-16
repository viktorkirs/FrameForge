using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FrameForge.Core;
using FrameForge.Encoding;
using FrameForge.UI;

internal static partial class Program
{
    static void RunEditorLayerOrderCoreTests()
    {
        Test("track reordering preserves every clip's time, selection, linked audio and undo history", () =>
        {
            var timeline = new VideoEditTimeline(TimeSpan.FromSeconds(4), Path.Combine(_output, "video.mp4"));
            Guid image = timeline.AddImageLayer(Path.Combine(_output, "image.png"), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1), "Image")!.Value;
            Guid audio = timeline.AddTrack(VideoEditTrackKind.Audio, "Audio");
            timeline.AddSource(Path.Combine(_output, "audio.wav"), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(.5), VideoEditTrackKind.Audio);
            Guid text = timeline.AddTextLayer(new() { Content = "Title" }, TimeSpan.FromSeconds(2), TimeSpan.Zero, "Text")!.Value;
            VideoEditSnapshot before = timeline.Snapshot(); Guid selected = timeline.SelectedClip!.Id;
            Check(timeline.LayerOrder.SequenceEqual(new[] { text, audio, image, Guid.Empty }), "A newly added layer did not enter the front of the stack");
            Check(timeline.MoveTrack(image, 0) && timeline.LayerOrder[0] == image, "Image could not move above text");
            Check(timeline.SelectedClip!.Id == selected && timeline.Clips.SequenceEqual(before.Clips)
                && timeline.Tracks.SelectMany(track => track.Clips).SequenceEqual(before.Tracks.SelectMany(track => track.Clips)), "Reordering shifted, replaced or selected another clip");
            Check(timeline.Undo() && timeline.LayerOrder.SequenceEqual(before.LayerOrder) && timeline.Redo() && timeline.LayerOrder[0] == image, "Layer order is not undoable");
            Check(timeline.MoveTrack(null, 0) && timeline.LayerOrder[0] == Guid.Empty && timeline.Clips.SequenceEqual(before.Clips), "Linked main video/audio cannot move together");
            string path = Path.Combine(_output, "ordered-layers.ffproj"); VideoEditProjectFile.Save(path, new(timeline.Snapshot(), 320, 180, 30));
            var loaded = VideoEditProjectFile.Load(path);
            Check(loaded.Version == 4 && loaded.Timeline.LayerOrder.SequenceEqual(timeline.LayerOrder), "Saved project lost the visible stack order");
            VideoEditTimeline restored = VideoEditTimeline.FromSnapshot(loaded.Timeline);
            restored.SelectedTrackId = image; restored.SelectedIndex = 0; restored.RemoveSelectedTrack();
            Check(!restored.LayerOrder.Contains(image) && restored.Undo() && restored.LayerOrder.SequenceEqual(timeline.LayerOrder), "Deleting and restoring a track broke its layer position");
            bool rejected = false;
            try { VideoEditProjectFile.Validate(loaded with { Timeline = loaded.Timeline with { LayerOrder = [Guid.Empty, Guid.Empty] } }); }
            catch (InvalidDataException) { rejected = true; }
            Check(rejected, "Duplicate or missing layer ids were accepted");
        });
    }

    static void RunEditorLayerOrderMediaTests(string source, string silent)
    {
        string image = CreateAlphaImage();
        var fixture = new VideoEditTimeline(TimeSpan.FromSeconds(6), source); fixture.DeleteSelected();
        Guid imageId = fixture.AddImageLayer(image, TimeSpan.FromSeconds(2), TimeSpan.Zero, "Image above video")!.Value;
        fixture.SetSelectedTransform(new());
        Guid videoId = fixture.AddTrack(VideoEditTrackKind.Video, "Video below image");
        fixture.AddSource(silent, TimeSpan.FromSeconds(6), TimeSpan.Zero, VideoEditTrackKind.Video);
        fixture.Trim(0, TimeSpan.Zero, TimeSpan.FromSeconds(1));
        string legacy = Path.Combine(_output, "legacy-image-above-video.ffproj");
        VideoEditProjectFile.Save(legacy, new(fixture.Snapshot(), 320, 180, 30));
        JsonNode old = JsonNode.Parse(File.ReadAllText(legacy))!; old["version"] = 3; old["timeline"]!.AsObject().Remove("layerOrder");
        File.WriteAllText(legacy, old.ToJsonString());
        VideoEditProject project = VideoEditProjectFile.Load(legacy);
        Test("an old image-above-video project keeps the upper image visible during and after the video", () =>
        {
            Check(project.Timeline.LayerOrder.SequenceEqual(new[] { imageId, videoId, Guid.Empty }), "Version-three layers did not adopt their visible row order");
            string output = Path.Combine(_output, "editor-upper-image.mp4");
            Wait(VideoEditorMedia.ExportAsync(project.Timeline, output, 320, 180, 30));
            CheckColor(Mp4Probe.Read(output, 15, maxSamples: 15), .375, .5, 0, "Lower video obscures the upper image");
            CheckColor(Mp4Probe.Read(output, 45, maxSamples: 45), .375, .5, 0, "Image changes after the video ends");
            CheckColor(Mp4Probe.Read(output, 15, maxSamples: 15), .15, .5, 2, "PNG transparency no longer shows the lower video");
            var timeline = VideoEditTimeline.FromSnapshot(project.Timeline);
            timeline.MoveTrack(videoId, 0);
            string reversed = Path.Combine(_output, "editor-upper-video.mp4");
            Wait(VideoEditorMedia.ExportAsync(timeline.Snapshot(), reversed, 320, 180, 30));
            CheckColor(Mp4Probe.Read(reversed, 15, maxSamples: 15), .375, .5, 2, "Moving the video up did not change exported overlap");
        });
        Test("dragging a track grip changes preview stacking, keeps time and can undo or cancel", () =>
        {
            Loc.Set(AppLanguage.Russian);
            var editor = new VideoEditorWindow(legacy); editor.Show();
            BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
            VideoEditTimeline? timeline = null;
            try
            {
                for (int i = 0; i < 100 && typeof(VideoEditorWindow).GetField("_loading", flags)!.GetValue(editor) is true; i++) Wait(Task.Delay(50));
                timeline = (VideoEditTimeline)typeof(VideoEditorWindow).GetField("_timeline", flags)!.GetValue(editor)!;
                var canvas = (VideoTimelineControl)editor.FindName("TimelineCanvas");
                typeof(VideoEditorWindow).GetMethod("Seek", flags)!.Invoke(editor, [TimeSpan.FromSeconds(.5)]);
                bool PreviewIs(int channel)
                {
                    for (int attempt = 0; attempt < 30; attempt++)
                    {
                        Wait(Task.Delay(40)); editor.UpdateLayout();
                        var preview = (Canvas)editor.FindName("PreviewCanvas");
                        var border = (System.Windows.Shapes.Rectangle)editor.FindName("OutputCanvasBorder");
                        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(preview.ActualWidth), (int)Math.Ceiling(preview.ActualHeight), 96, 96, PixelFormats.Pbgra32); bitmap.Render(preview);
                        int x = (int)(Canvas.GetLeft(border) + border.Width * .375), y = (int)(Canvas.GetTop(border) + border.Height * .5);
                        byte[] pixel = new byte[4]; bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
                        if (pixel[channel] > 150 && pixel[channel] > pixel[(channel + 1) % 3] + 80) return true;
                    }
                    return false;
                }
                Check(PreviewIs(0), "The preview still puts later-created video over the upper image");
                void Drag(Guid id, int destination, bool cancel = false)
                {
                    var headers = (Canvas)editor.FindName("TrackHeaders");
                    Thumb grip = headers.Children.OfType<StackPanel>().SelectMany(panel => panel.Children.OfType<Thumb>()).Single(thumb => Equals(thumb.Tag, id));
                    Guid target = timeline.LayerOrder[destination];
                    double from = canvas.LaneY(canvas.TrackTop(id == Guid.Empty ? null : id) + (id == Guid.Empty ? 75 : 32));
                    double to = canvas.LaneY(canvas.TrackTop(target == Guid.Empty ? null : target) + (target == Guid.Empty ? 75 : 32));
                    grip.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
                    grip.RaiseEvent(new DragDeltaEventArgs(0, (to - from) / 2) { RoutedEvent = Thumb.DragDeltaEvent });
                    grip.RaiseEvent(new DragDeltaEventArgs(0, to - from) { RoutedEvent = Thumb.DragDeltaEvent });
                    Check(canvas.TrackInsertionY != null, "Track drag has no insertion marker");
                    grip.RaiseEvent(new DragCompletedEventArgs(0, to - from, cancel) { RoutedEvent = Thumb.DragCompletedEvent });
                }
                Drag(imageId, 1);
                Check(timeline.LayerOrder[1] == imageId && timeline.LayerOrder[2] == Guid.Empty, "Repeated drag movements overshot the destination row");
                ((Button)editor.FindName("UndoButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Drag(videoId, 0);
                Check(timeline.LayerOrder[0] == videoId && ((Slider)editor.FindName("SeekSlider")).Value == .5 && PreviewIs(2), "Track drag did not change the visible overlap without seeking");
                ((Button)editor.FindName("UndoButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(timeline.LayerOrder[0] == imageId && PreviewIs(0), "Undo did not restore the image in front");
                Guid state = timeline.StateId; Drag(videoId, 0, cancel: true);
                Check(timeline.StateId == state && canvas.TrackInsertionY == null, "Canceled reorder changed the project or left a marker");
                Drag(Guid.Empty, 0); Check(timeline.LayerOrder[0] == Guid.Empty, "The linked main track has no working reorder grip");
                ((Button)editor.FindName("UndoButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                editor.UpdateLayout(); Wait(Task.Delay(100)); SaveWindow(editor, "editor-correct-layer-order.png");
            }
            finally
            {
                if (timeline != null) typeof(VideoEditorWindow).GetField("_savedProjectState", flags)!.SetValue(editor, timeline.StateId);
                typeof(VideoEditorWindow).GetField("_projectNeedsSave", flags)!.SetValue(editor, false); editor.Close();
            }
        });
        Test("modal glass dims and blurs the editor and its owner, then restores both after cancel", () =>
        {
            var owner = new Window { Width = 650, Height = 350, Content = new TextBlock { Text = "FrameForge recording workspace", FontSize = 28 } }; owner.Show();
            var editor = new VideoEditorWindow(legacy) { Owner = owner }; editor.Show();
            BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
            Exception? failure = null;
            try
            {
                for (int i = 0; i < 100 && typeof(VideoEditorWindow).GetField("_loading", flags)!.GetValue(editor) is true; i++) Wait(Task.Delay(50));
                UIElement editorRoot = (UIElement)editor.Content, ownerRoot = (UIElement)owner.Content;
                Effect? original = editorRoot.Effect;
                Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    var dialog = Application.Current.Windows.OfType<AppDialog>().Single();
                    try
                    {
                        Check(editorRoot.Effect is BlurEffect { Radius: >= 8 } && ownerRoot.Effect is BlurEffect, "Modal did not frost the background windows");
                        Check(AdornerLayer.GetAdornerLayer(editorRoot)?.GetAdorners(editorRoot)?.Any(a => a.GetType().Name == "FrostedGlass") == true, "Blur has no dark glass tint");
                        using (ModalBackdrop.Begin(dialog)) Check(((UIElement)dialog.Content).Effect is BlurEffect, "Nested modal did not shade its parent dialog");
                        Check(editorRoot.Effect is BlurEffect && ((UIElement)dialog.Content).Effect == null, "Nested modal removed its parent's backdrop too early");
                        SaveWindow(editor, "editor-modal-frosted.png"); SaveWindow(dialog, "editor-modal-dialog.png");
                    }
                    catch (Exception ex) { failure = ex; }
                    finally { ((Button)dialog.FindName("CancelAction")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
                }, DispatcherPriority.ApplicationIdle);
                Check(AppDialog.ConfirmSave(editor) == MessageBoxResult.Cancel, "Modal cancel changed the confirmation result");
                if (failure != null) throw failure;
                Check(ReferenceEquals(editorRoot.Effect, original) && ownerRoot.Effect == null &&
                    AdornerLayer.GetAdornerLayer(editorRoot)?.GetAdorners(editorRoot)?.Any(a => a.GetType().Name == "FrostedGlass") != true, "Closing the modal left the editor blurred or shaded");
            }
            finally { editor.Close(); owner.Close(); }
        });
    }
}
