using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FrameForge.Core;
using FrameForge.Encoding;
using FrameForge.UI;

internal static partial class Program
{
    static void RunEditorVisualCoreTests()
    {
        Test("image and editable text layers retain timing, style and independent copy history", () =>
        {
            var timeline = new VideoEditTimeline(TimeSpan.FromSeconds(8), Path.Combine(_output, "source.mp4"));
            timeline.AddImageLayer(Path.Combine(_output, "transparent.png"), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1), "PNG");
            Check(timeline.SelectedClip!.Kind == VideoEditClipKind.Image && timeline.SelectedClip.OriginalDuration == TimeSpan.FromDays(7), "Still image cannot extend past its initial duration");
            Check(timeline.Trim(0, TimeSpan.FromSeconds(.5), TimeSpan.FromSeconds(7)) && timeline.SelectedStart.TotalSeconds == 1.5, "Image edge trim changed the wrong boundary");
            var text = new VideoEditText { Content = "Прозрачность\nText", FontFamily = "Consolas", FontSize = 42, Color = "#80E0A030", Bold = true, Italic = true, Underline = true, Alignment = VideoTextAlignment.Right, ReferenceWidth = 1080 };
            timeline.AddTextLayer(text, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(2), "Title");
            Guid id = timeline.SelectedClip!.Id;
            Check(timeline.Split(TimeSpan.FromSeconds(4)) && timeline.SelectedClip.Text == text, "Splitting a text layer lost formatting");
            Check(timeline.SetSelectedText(text with { FontSize = 64 }) && timeline.Undo() && timeline.SelectedClip!.Text == text, "Text formatting cannot be undone");
            VideoEditCopy copy = timeline.CopySelection()!;
            Check(copy.IsValid && timeline.Paste(copy, TimeSpan.FromSeconds(3), forceNewTracks: true), "Editable text did not copy without an external source path");
            Check(timeline.SelectedClip!.Id != id && timeline.SelectedClip.Text == text && timeline.SelectedStart.TotalSeconds == 3, "Text copy is linked to the old clip or moved");
            Check(timeline.Clips[0].Position == TimeSpan.Zero, "Adding layers moved the underlying video");
            string projectPath = Path.Combine(_output, "visual-layers.ffproj");
            VideoEditProjectFile.Save(projectPath, new(timeline.Snapshot(), 1080, 1920, 30));
            VideoEditProject restored = VideoEditProjectFile.Load(projectPath);
            Check(restored.Version == 4 && restored.Timeline.Tracks.Last().Clips.Single().Text == text, "Saved text layer lost editable formatting");
            Check(VideoEditProjectFile.MediaPaths(restored.Timeline).Count() == 2, "Text created an external media dependency");
            Check(timeline.RemoveRange(TimeSpan.FromSeconds(3.5), TimeSpan.FromSeconds(4.5)) && timeline.Undo(), "Range cutting failed with still layers");
        });
        Test("invalid text, clip kinds and still audio lanes are rejected before saving", () =>
        {
            var timeline = new VideoEditTimeline(TimeSpan.FromSeconds(5), Path.Combine(_output, "source.mp4"));
            Check(timeline.AddTextLayer(new() { FontFamily = "https://example.org/font" }, TimeSpan.FromSeconds(1), TimeSpan.Zero, "Text") == null, "A remote font was accepted");
            Check(timeline.AddTextLayer(new() { Color = "red" }, TimeSpan.FromSeconds(1), TimeSpan.Zero, "Text") == null, "Invalid serialized color accepted");
            timeline.AddTextLayer(new() { Content = "Title" }, TimeSpan.FromSeconds(1), TimeSpan.Zero, "Text");
            var snapshot = timeline.Snapshot(); var track = snapshot.Tracks.Single();
            bool Reject(VideoEditTrack lane)
            { try { VideoEditProjectFile.Validate(new(snapshot with { Tracks = [lane] }, 320, 180, 30)); return false; } catch (InvalidDataException) { return true; } }
            Check(Reject(track with { Kind = VideoEditTrackKind.Audio }), "Still layer accepted on audio-only lane");
            Check(Reject(track with { Clips = [track.Clips[0] with { Kind = (VideoEditClipKind)999 }] }), "Unknown clip type accepted");
            Check(Reject(track with { Clips = [track.Clips[0] with { Text = new() { FontSize = double.NaN } }] }), "Invalid text size accepted");
        });
    }

    static string CreateAlphaImage()
    {
        byte[] pixels = new byte[320 * 180 * 4];
        for (int y = 45; y < 135; y++) for (int x = 80; x < 240; x++)
        {
            int p = (y * 320 + x) * 4;
            if (x < 160) { pixels[p] = 240; pixels[p + 3] = 255; }
            else { pixels[p + 1] = 240; pixels[p + 3] = 128; }
        }
        string path = Path.Combine(_output, "editor-alpha.png");
        var bitmap = BitmapSource.Create(320, 180, 96, 96, PixelFormats.Bgra32, null, pixels, 320 * 4);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(path); encoder.Save(output); return path;
    }

    static void RunEditorVisualMediaTests(string source, string silent, string wave)
    {
        string image = CreateAlphaImage();
        Test("PNG export retains transparent holes, partial alpha and the layer's time interval", () =>
        {
            var timeline = new VideoEditTimeline(TimeSpan.FromSeconds(6), source);
            timeline.Trim(0, TimeSpan.Zero, TimeSpan.FromSeconds(2));
            timeline.AddImageLayer(image, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(.5), "PNG");
            timeline.SetSelectedTransform(new());
            string output = Path.Combine(_output, "editor-alpha-export.mp4");
            Wait(VideoEditorMedia.ExportAsync(timeline.Snapshot(), output, 320, 180, 30));
            Mp4Probe frame = Mp4Probe.Read(output, 23, maxSamples: 23);
            CheckColor(frame, .15, .5, 2, "A transparent PNG area hides the underlying video");
            CheckColor(frame, .375, .5, 0, "Opaque PNG pixels did not appear");
            int p = ((int)(frame.PreviewHeight * .5) * frame.PreviewWidth + (int)(frame.PreviewWidth * .625)) * 4;
            Check(frame.PreviewPixels![p + 1] is > 80 and < 170 && frame.PreviewPixels[p + 2] is > 80 and < 170, $"Half-transparent pixels were flattened instead of blended ({frame.PreviewPixels[p]}/{frame.PreviewPixels[p + 1]}/{frame.PreviewPixels[p + 2]})");
            CheckColor(Mp4Probe.Read(output, 7, maxSamples: 7), .375, .5, 2, "Image started before its position");
            CheckColor(Mp4Probe.Read(output, 52, maxSamples: 52), .375, .5, 2, "Image remained visible after its end");
            timeline.SetSelectedFades(TimeSpan.FromSeconds(.5), TimeSpan.FromSeconds(.5));
            string faded = Path.Combine(_output, "editor-alpha-fade.mp4");
            Wait(VideoEditorMedia.ExportAsync(timeline.Snapshot(), faded, 320, 180, 30));
            Mp4Probe fade = Mp4Probe.Read(faded, 23, maxSamples: 23);
            int fadePixel = ((int)(fade.PreviewHeight * .5) * fade.PreviewWidth + (int)(fade.PreviewWidth * .625)) * 4;
            Check(fade.PreviewPixels![fadePixel + 1] is > 35 and < 100 && fade.PreviewPixels[fadePixel + 2] is > 150 and < 220, "Clip fade did not multiply the image's own alpha correctly");
            timeline.SetSelectedTransform(new() { Left = .25, Right = .25, QuarterTurns = 1, Scale = .5, OffsetX = .2 });
            string rotated = Path.Combine(_output, "editor-alpha-rotated.mp4");
            Wait(VideoEditorMedia.ExportAsync(timeline.Snapshot(), rotated, 320, 180, 30));
            CheckColor(Mp4Probe.Read(rotated, 23, maxSamples: 23), .15, .5, 2, "Cropping and rotating a PNG flattened the surrounding video");
            Check(!Directory.GetFiles(_output, ".frameforge-layer-*.png").Any(), "Export leaked intermediate image files");
        });
        Test("formatted text renders as transparent pixels and reaches the encoded video", () =>
        {
            var text = new VideoEditText { Content = "Title\nТекст", FontFamily = "Consolas", FontSize = 28, ReferenceWidth = 320, Color = "#FFFFD166", Bold = true, Italic = true, Underline = true };
            BitmapSource bitmap = VideoEditorVisuals.RenderText(text);
            byte[] pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4]; bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
            Check(pixels.Where((_, i) => i % 4 == 3).Any(alpha => alpha == 0) && pixels.Where((_, i) => i % 4 == 3).Any(alpha => alpha == 255), "Text raster has an opaque background or no glyphs");
            Check(VideoEditorVisuals.RenderText(text with { FontSize = 48 }).PixelHeight > bitmap.PixelHeight, "Font size does not change the visible text");
            var timeline = new VideoEditTimeline(TimeSpan.FromSeconds(6), source);
            timeline.Trim(0, TimeSpan.Zero, TimeSpan.FromSeconds(1));
            timeline.AddTextLayer(text, TimeSpan.FromSeconds(1), TimeSpan.Zero, "Title");
            string output = Path.Combine(_output, "editor-text-export.mp4"); Wait(VideoEditorMedia.ExportAsync(timeline.Snapshot(), output, 320, 180, 30));
            Mp4Probe frame = Mp4Probe.Read(output, 15, maxSamples: 15);
            CheckColor(frame, .02, .02, 2, "Text background obscures the underlying video");
            int yellow = 0;
            for (int p = 0; p < frame.PreviewPixels!.Length; p += 4)
                if (frame.PreviewPixels[p + 2] > 170 && frame.PreviewPixels[p + 1] > 130 && frame.PreviewPixels[p] < 150) yellow++;
            Check(yellow > 100, "Formatted text did not reach the export in its chosen color");
        });
        Test("image drops, live text formatting, copying and project resume share the same layer previews", () =>
        {
            Loc.Set(AppLanguage.Russian);
            var editor = new VideoEditorWindow(source); editor.Show();
            BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            object? Invoke(string name, params object?[] args) => typeof(VideoEditorWindow).GetMethod(name, flags)!.Invoke(editor, args);
            VideoEditTimeline? timeline = null;
            string project = Path.Combine(_output, "editor-visual-resume.ffproj");
            try
            {
                for (int i = 0; i < 100 && typeof(VideoEditorWindow).GetField("_loading", flags)!.GetValue(editor) is true; i++) Wait(Task.Delay(50));
                var canvas = (VideoTimelineControl)editor.FindName("TimelineCanvas");
                var data = new DataObject(DataFormats.FileDrop, new[] { silent, wave, image });
                ConstructorInfo dragConstructor = typeof(DragEventArgs).GetConstructors(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance).Single(c => c.GetParameters().Length == 5);
                DragEventArgs Drag(RoutedEvent route)
                {
                    var args = (DragEventArgs)dragConstructor.Invoke([data, DragDropKeyStates.None, DragDropEffects.Copy, canvas, new Point(12 + canvas.PixelsPerSecond * 1.25, 45)]);
                    args.RoutedEvent = route; return args;
                }
                DragEventArgs over = Drag(DragDrop.PreviewDragOverEvent); canvas.RaiseEvent(over);
                Check(over.Handled && over.Effects == DragDropEffects.Copy, "Explorer file-drop feedback does not accept supported files");
                canvas.RaiseEvent(Drag(DragDrop.PreviewDropEvent));
                for (int i = 0; i < 100 && typeof(VideoEditorWindow).GetField("_loading", flags)!.GetValue(editor) is true; i++) Wait(Task.Delay(50));
                timeline = (VideoEditTimeline)typeof(VideoEditorWindow).GetField("_timeline", flags)!.GetValue(editor)!;
                Check(editor.AllowDrop && timeline.Tracks.Count == 3 && timeline.Tracks.All(track => Math.Abs(track.Clips.Single().Position.TotalSeconds - 1.25) < .0001), "A dropped file did not become an independent layer at the pointer's time");
                ((Button)editor.FindName("AddTextButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                ((TextBox)editor.FindName("TextContentBox")).Text = "Редактируемый текст\nFrameForge";
                ((ComboBox)editor.FindName("TextFontBox")).SelectedItem = "Consolas";
                ((TextBox)editor.FindName("TextSizeBox")).Text = "26";
                ((CheckBox)editor.FindName("TextBoldBox")).IsChecked = true;
                ((CheckBox)editor.FindName("TextItalicBox")).IsChecked = true;
                ((CheckBox)editor.FindName("TextUnderlineBox")).IsChecked = true;
                ((TextBox)editor.FindName("TextColorBox")).Text = "#C0FFD166";
                ((ComboBox)editor.FindName("TextAlignmentBox")).SelectedIndex = 0;
                VideoEditText text = timeline.SelectedClip!.Text!;
                Check(text.Content.Contains("FrameForge") && text.FontFamily == "Consolas" && text.FontSize == 26 && text.Color == "#C0FFD166" && text.Bold && text.Italic && text.Underline && text.Alignment == VideoTextAlignment.Left, "Formatting controls did not change the editable text model");
                Check(((FrameworkElement)editor.FindName("FrameAdorner")).Visibility == Visibility.Visible, "Text cannot be moved or resized in the preview");
                ((TextBox)editor.FindName("LayerDurationBox")).Text = "2.5"; Invoke("ApplyLayerDuration_Click", editor, new RoutedEventArgs());
                Check(timeline.SelectedClip.Duration.TotalSeconds == 2.5, "The text layer duration was not adjustable");
                timeline.SetSelectedTransform(new() { OffsetY = .25 }); Invoke("AfterEdit", true, null);
                Invoke("Copy_Click", editor, new RoutedEventArgs()); Wait((Task)Invoke("PasteAsync", true)!);
                Check(timeline.Tracks.Count == 5 && timeline.SelectedClip!.Text == text, "Text layer paste tried to open a nonexistent media path");
                Check((bool)Invoke("SaveProjectTo", project)!, "Cannot save visual project");
                editor.Height = 1100;
                ((RowDefinition)editor.FindName("PreviewRow")).Height = new GridLength(3, GridUnitType.Star);
                ((RowDefinition)editor.FindName("TracksRow")).Height = new GridLength(2, GridUnitType.Star);
                ((Slider)editor.FindName("TrackHeightSlider")).Value = .5;
                editor.UpdateLayout(); Wait(Task.Delay(100));
                SaveWindow(editor, "editor-image-text-layers.png");
            }
            finally
            {
                if (timeline != null) typeof(VideoEditorWindow).GetField("_savedProjectState", flags)!.SetValue(editor, timeline.StateId);
                typeof(VideoEditorWindow).GetField("_projectNeedsSave", flags)!.SetValue(editor, false); editor.Close();
            }
            var resumed = new VideoEditorWindow(project); resumed.Show();
            try
            {
                for (int i = 0; i < 100 && typeof(VideoEditorWindow).GetField("_loading", flags)!.GetValue(resumed) is true; i++) Wait(Task.Delay(50));
                var restored = (VideoEditTimeline?)typeof(VideoEditorWindow).GetField("_timeline", flags)!.GetValue(resumed);
                Check(restored?.Tracks.Count == 5 && restored.Tracks.Last().Clips.Single().Text!.Color == "#C0FFD166", "Project resume lost still layers: " + ((TextBlock)resumed.FindName("StatusText")).Text);
                restored!.SelectedTrackId = restored.Tracks.Last().Id; restored.SelectedIndex = 0;
                typeof(VideoEditorWindow).GetMethod("AfterEdit", flags)!.Invoke(resumed, [true, null]);
                Check(((StackPanel)resumed.FindName("TextTools")).Visibility == Visibility.Visible, "Reloaded text is no longer editable");
            }
            finally { resumed.Close(); }
        });
        Test("an empty editor accepts still layers and resumes a text-only project without video files", () =>
        {
            var editor = new VideoEditorWindow(); editor.Show();
            BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
            string project = Path.Combine(_output, "editor-text-only.ffproj");
            VideoEditTimeline? timeline = null;
            try
            {
                ((Button)editor.FindName("AddTextButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                timeline = (VideoEditTimeline)typeof(VideoEditorWindow).GetField("_timeline", flags)!.GetValue(editor)!;
                Check(timeline.Clips.Count == 0 && timeline.Tracks.Single().Clips.Single().Kind == VideoEditClipKind.Text, "A video file is required to create text");
                Check((bool)typeof(VideoEditorWindow).GetMethod("SaveProjectTo", flags)!.Invoke(editor, [project])!, "Cannot save a text-only project");
                Check(!VideoEditProjectFile.MediaPaths(VideoEditProjectFile.Load(project).Timeline).Any(), "Text-only project references generated media");
                ((TextBox)editor.FindName("LayerDurationBox")).Text = "0.5";
                typeof(VideoEditorWindow).GetMethod("ApplyLayerDuration_Click", flags)!.Invoke(editor, [editor, new RoutedEventArgs()]);
                Wait(VideoEditorMedia.ExportAsync(timeline.Snapshot(), Path.Combine(_output, "editor-text-only.mp4"), 320, 180, 30));
            }
            finally
            {
                if (timeline != null) typeof(VideoEditorWindow).GetField("_savedProjectState", flags)!.SetValue(editor, timeline.StateId);
                typeof(VideoEditorWindow).GetField("_projectNeedsSave", flags)!.SetValue(editor, false); editor.Close();
            }
            var resumed = new VideoEditorWindow(project); resumed.Show();
            try
            {
                for (int i = 0; i < 100 && typeof(VideoEditorWindow).GetField("_loading", flags)!.GetValue(resumed) is true; i++) Wait(Task.Delay(50));
                Check(typeof(VideoEditorWindow).GetField("_timeline", flags)!.GetValue(resumed) is VideoEditTimeline { Clips.Count: 0, Tracks.Count: 1 }, "Text-only project did not resume");
            }
            finally { resumed.Close(); }
        });
    }
}
