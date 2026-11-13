using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FrameForge.Capture;
using FrameForge.Core;
using FrameForge.Encoding;
using FrameForge.UI;

internal static partial class Program
{
    static void RunVideoEditTimelineTests()
    {
        Test("montage maps cuts, trim and reordered clips to source time", () =>
        {
            var timeline = new VideoEditTimeline(TimeSpan.FromSeconds(10));
            Check(!timeline.Split(TimeSpan.Zero) && !timeline.Split(timeline.Duration), "Accepted an empty cut");
            Check(timeline.Split(TimeSpan.FromSeconds(3)) && timeline.Split(TimeSpan.FromSeconds(7)), "Split failed");
            Check(timeline.Clips.Count == 3 && timeline.Duration == TimeSpan.FromSeconds(10), "Split changed duration");
            Check(timeline.Resolve(TimeSpan.FromSeconds(3)) is { ClipIndex: 1, SourceTime.TotalSeconds: 3 }, "Cut boundary maps to previous clip");
            Check(timeline.Trim(1, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(6)), "Trim failed");
            Check(timeline.Duration == TimeSpan.FromSeconds(8), "Trim did not close gap");
            Check(timeline.Move(2, 0), "Reorder failed");
            Check(timeline.Resolve(TimeSpan.Zero) is { SourceTime.TotalSeconds: 7 }, "Reorder did not move source");
            Check(timeline.Resolve(TimeSpan.FromSeconds(3)) is { SourceTime.TotalSeconds: 0 }, "Next clip has wrong source time");
            Check(timeline.Resolve(timeline.Duration)!.Value.SourceTime < TimeSpan.FromSeconds(6), "End maps outside source interval");
            Check(!timeline.Trim(0, TimeSpan.FromSeconds(-1), TimeSpan.FromSeconds(2)) &&
                !timeline.Trim(0, TimeSpan.Zero, TimeSpan.FromSeconds(11)) &&
                !timeline.Trim(0, TimeSpan.Zero, TimeSpan.FromMilliseconds(1)), "Accepted invalid source bounds");
        });
        Test("montage history restores deletion, empty timelines, order and selection", () =>
        {
            var timeline = new VideoEditTimeline(TimeSpan.FromSeconds(6));
            timeline.Split(TimeSpan.FromSeconds(2));
            timeline.Split(TimeSpan.FromSeconds(4));
            VideoEditClip[] before = timeline.Clips.ToArray();
            timeline.Move(2, 0);
            timeline.DeleteSelected();
            Check(timeline.Duration == TimeSpan.FromSeconds(4), "Delete did not ripple");
            Check(timeline.Undo() && timeline.Undo() && timeline.Clips.SequenceEqual(before), "Undo lost source ranges or order");
            Check(timeline.Redo() && timeline.SelectedIndex == 0, "Redo lost selection");
            timeline.Trim(0, TimeSpan.FromSeconds(4.5), TimeSpan.FromSeconds(5.5));
            Check(!timeline.CanRedo, "New edit retained redo branch");
            while (timeline.Clips.Count > 0) timeline.DeleteSelected();
            Check(timeline.Duration == TimeSpan.Zero && timeline.Resolve(TimeSpan.Zero) == null, "Empty timeline mapped a clip");
            Check(!timeline.DeleteSelected() && !timeline.Move(0, 1), "Invalid edit changed empty timeline");
            Check(timeline.Undo() && timeline.Clips.Count == 1, "Could not restore last clip");
            while (timeline.Undo()) { }
            Check(!timeline.HasChanges && timeline.Duration == TimeSpan.FromSeconds(6), "Undo did not restore original");
        });
        Test("montage keeps exact ticks through repeated cuts", () =>
        {
            var timeline = new VideoEditTimeline(TimeSpan.FromHours(2));
            for (int i = 1; i < 100; i++) Check(timeline.Split(TimeSpan.FromTicks(i * 1_234_567L)), "Tick cut rejected");
            Check(timeline.Clips.Sum(clip => clip.Duration.Ticks) == TimeSpan.FromHours(2).Ticks, "Cuts accumulated rounding drift");
            Check(timeline.Clips.All(clip => clip.Duration >= VideoEditTimeline.MinimumDuration), "Cut generated a subframe interval");
        });
    }

    static void RunVideoEditorTests()
    {
        RunVideoEditTimelineTests();
        string source = Path.Combine(_output, "editor-source.mp4");
        string silent = Path.Combine(_output, "editor-silent.mp4");
        string frames = Path.Combine(_output, "editor-frames.mp4");
        Test("editor fixture encodes colors and distinct audio segments", () =>
        {
            CreateEditorSource(source, audio: true);
            CreateEditorSource(silent, audio: false);
            CreateEditorSource(frames, audio: false, everyFrame: true);
            Check(AudioProbe.Read(source).HasAudio && !AudioProbe.Read(silent).HasAudio, "Fixture audio tracks incorrect");
        });
        Test("editor waveform spans the recording, is bounded and supports cancellation", () =>
        {
            AudioWaveform waveform = AudioWaveform.Read(source, TimeSpan.FromSeconds(6), buckets: 240);
            Check(waveform.HasAudio && waveform.Peaks.Length == 240, "Invalid waveform storage");
            Check(waveform.Peaks.Take(70).Max() > .1 && waveform.Peaks.Skip(170).Max() > .1, "Waveform lost source head or tail");
            Check(!AudioWaveform.Read(silent, TimeSpan.FromSeconds(6)).HasAudio, "Silent recording invented audio");
            using var cancel = new CancellationTokenSource();
            cancel.Cancel();
            bool canceled = false;
            try { AudioWaveform.Read(source, TimeSpan.FromSeconds(6), cancel.Token); }
            catch (OperationCanceledException) { canceled = true; }
            Check(canceled, "Waveform ignored cancellation");
        });
        Test("precise MP4 export cuts and reorders linked video and audio", () =>
        {
            VideoEditorSource info = null!;
            Wait(Read());
            async Task Read() => info = await VideoEditorMedia.ReadAsync(source);
            Check(info.Width == 320 && info.Height == 180 && info.HasAudio, "Native source properties incorrect");
            var timeline = new VideoEditTimeline(info.Duration);
            timeline.Split(TimeSpan.FromSeconds(2));
            timeline.Split(TimeSpan.FromSeconds(4));
            timeline.SelectedIndex = 1;
            timeline.DeleteSelected();
            timeline.Trim(0, TimeSpan.FromSeconds(.4), TimeSpan.FromSeconds(1.6));
            timeline.Trim(1, TimeSpan.FromSeconds(4.2), TimeSpan.FromSeconds(5.6));
            timeline.Move(1, 0);
            string destination = Path.Combine(_output, "editor-montage.mp4");
            Wait(VideoEditorMedia.ExportAsync(source, timeline.Clips, destination));
            StreamTimeline video = MediaTimelineProbe.Read(destination);
            AudioProbe audio = AudioProbe.Read(destination);
            Check(Math.Abs(video.End100Ns / 1e7 - 2.6) < .1, "Export duration ignores trim or deletion");
            Check(audio.HasAudio && Math.Abs(audio.Duration100Ns - video.End100Ns) / 1e7 < .12, "Edited audio is missing or out of sync");
            Mp4Probe first = Mp4Probe.Read(destination, 5, maxSamples: 5);
            Mp4Probe last = Mp4Probe.Read(destination, 60, maxSamples: 60);
            Check(first.Width == 320 && first.Height == 180, "Export changed source resolution");
            Check(first.PreviewPixels![0] > first.PreviewPixels[2] + 100, "Reordered first video clip is not blue");
            Check(last.PreviewPixels![2] > last.PreviewPixels[0] + 100, "Last video clip is not red");
            float[] head = audio.PreviewMono.Skip(9600).Take(19200).ToArray();
            float[] tail = audio.PreviewMono.Skip(81600).Take(19200).ToArray();
            Check(ToneAmplitude(head, 1320) > .05 && ToneAmplitude(head, 440) < .01, "Audio head did not move with blue clip");
            Check(ToneAmplitude(tail, 440) > .05 && ToneAmplitude(tail, 1320) < .01, "Audio tail did not move with red clip");
        });
        Test("silent MP4 exports and failures preserve source and existing destination", () =>
        {
            var timeline = new VideoEditTimeline(TimeSpan.FromSeconds(6));
            timeline.Trim(0, TimeSpan.FromSeconds(.3), TimeSpan.FromSeconds(1.3));
            string destination = Path.Combine(_output, "editor-silent-trim.mp4");
            Wait(VideoEditorMedia.ExportAsync(silent, timeline.Clips, destination));
            Check(!AudioProbe.Read(destination).HasAudio, "Silent export acquired an audio track");
            byte[] original = File.ReadAllBytes(source);
            bool protectedSource = false;
            try { Wait(VideoEditorMedia.ExportAsync(source, timeline.Clips, source)); }
            catch (InvalidOperationException) { protectedSource = true; }
            Check(protectedSource && original.SequenceEqual(File.ReadAllBytes(source)), "Original recording was overwritten");
            byte[] saved = File.ReadAllBytes(destination);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            bool canceled = false;
            try { Wait(VideoEditorMedia.ExportAsync(source, timeline.Clips, destination, cancellationToken: cancellation.Token)); }
            catch (OperationCanceledException) { canceled = true; }
            Check(canceled && saved.SequenceEqual(File.ReadAllBytes(destination)), "Canceled export replaced destination");
            using var duringRender = new CancellationTokenSource();
            bool canceledDuringRender = false;
            try { Wait(VideoEditorMedia.ExportAsync(source, timeline.Clips, destination, new Progress<double>(_ => duringRender.Cancel()), duringRender.Token)); }
            catch (OperationCanceledException) { canceledDuringRender = true; }
            Check(canceledDuringRender && saved.SequenceEqual(File.ReadAllBytes(destination)), "Canceling a running render replaced destination");
            Check(!Directory.GetFiles(_output, ".frameforge-*.mp4").Any(), "Temporary exports were left behind");
        });
        Test("editor paused scrubbing, playback, controls and translated layouts work", () => EditorWindowChecks(source));
        Test("timeline hover distinguishes movement and trimming and highlights handles", EditorHoverChecks);
        Test("trimmed preview steps frames, excludes the out point and keeps the latest seek", () => EditorTrimFrameChecks(frames));
    }

    static void CreateEditorSource(string path, bool audio, bool everyFrame = false)
    {
        using GraphicsDevice device = GraphicsDevice.CreateForMonitor(new MonitorHandle(0));
        using var writer = Mp4VideoWriter.Open(device.Device, path, 320, 180, 30, 1_000_000, EncoderPreference.Software, out _, audio);
        var pixels = new byte[320 * 180 * 4];
        var pcm = new short[1600 * 2];
        for (int frame = 0; frame < 180; frame++)
        {
            int section = everyFrame ? frame % 3 : frame / 60;
            for (int i = 0; i < pixels.Length; i += 4)
            {
                pixels[i] = (byte)(section == 2 ? 240 : 15);
                pixels[i + 1] = (byte)(section == 1 ? 240 : 15);
                pixels[i + 2] = (byte)(section == 0 ? 240 : 15);
                pixels[i + 3] = 255;
            }
            writer.WriteBgra(pixels, frame * 10_000_000L / 30, (frame + 1) * 10_000_000L / 30 - frame * 10_000_000L / 30, frame == 0);
            if (audio)
            {
                for (int sample = 0; sample < 1600; sample++)
                    pcm[sample * 2] = pcm[sample * 2 + 1] = (short)(.2 * short.MaxValue * Math.Sin((frame * 1600L + sample) * 2 * Math.PI * (section + 1) * 440 / 48000));
                writer.WriteAudio(MemoryMarshal.AsBytes(pcm.AsSpan()), frame * 1600L);
            }
        }
        writer.Finish();
    }

    static void EditorWindowChecks(string source)
    {
        Loc.Set(AppLanguage.Russian);
        var editor = new VideoEditorWindow(source);
        editor.Show();
        try
        {
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            for (int attempt = 0; attempt < 100 && typeof(VideoEditorWindow).GetField("_mediaReady", flags)!.GetValue(editor) is false; attempt++)
                Wait(Task.Delay(100));
            Check((bool)typeof(VideoEditorWindow).GetField("_mediaReady", flags)!.GetValue(editor)!, "Video preview did not open");
            var timeline = (VideoEditTimeline)typeof(VideoEditorWindow).GetField("_timeline", flags)!.GetValue(editor)!;
            var seek = (Slider)editor.FindName("SeekSlider");
            var player = (MediaElement)editor.FindName("Player");
            void Trim(double start, double end)
            {
                ((TextBox)editor.FindName("TrimInBox")).Text = TimeSpan.FromSeconds(start).ToString(@"hh\:mm\:ss\.fff");
                ((TextBox)editor.FindName("TrimOutBox")).Text = TimeSpan.FromSeconds(end).ToString(@"hh\:mm\:ss\.fff");
                typeof(VideoEditorWindow).GetMethod("Trim_Click", flags)!.Invoke(editor, [editor, new RoutedEventArgs()]);
            }
            Trim(1, 5);
            seek.Value = .5;
            Check(WaitForPreviewColor(player, 2), "Left trim broke paused seeking to a red source frame");
            seek.Value = 1.5;
            Check(WaitForPreviewColor(player, 1), "Left trim broke paused seeking to a green source frame");
            seek.Value = 3.5;
            Check(WaitForPreviewColor(player, 0), "Right trim broke paused seeking to a blue source frame");
            seek.Value = 4;
            Check(WaitForPreviewColor(player, 0), "End of a trimmed clip did not show its last retained frame");
            Trim(2.5, 3.5);
            seek.Value = .8;
            Check(WaitForPreviewColor(player, 1), "Repeated trim broke paused preview");
            ((Button)editor.FindName("UndoButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            ((Button)editor.FindName("UndoButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            seek.Value = .5;
            Check(WaitForPreviewColor(player, 2), "Paused preview did not show the red source frame");
            seek.Value = 2.3;
            Check(WaitForPreviewColor(player, 1), "Paused scrubbing did not update to the green frame");
            seek.Value = 2;
            ((Button)editor.FindName("SplitButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(timeline.Clips.Count == 2, "Split button did not cut at playhead");
            ((Button)editor.FindName("MoveLeftButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(timeline.Clips[0].Start == TimeSpan.FromSeconds(2), "Move button did not reorder blocks");
            Check(Math.Abs(player.Position.TotalSeconds - 2) < .1, "Preview did not seek into moved source clip");
            ((Button)editor.FindName("PlayButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            for (int attempt = 0; attempt < 40 && player.Position <= TimeSpan.FromSeconds(2.1); attempt++) Wait(Task.Delay(50));
            Check(player.Position > TimeSpan.FromSeconds(2.1), "Preview did not advance during playback");
            ((Button)editor.FindName("PlayButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            seek.Value = 3.9;
            ((Button)editor.FindName("PlayButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            for (int attempt = 0; attempt < 40 && !(seek.Value > 4 && player.Position < TimeSpan.FromSeconds(1)); attempt++) Wait(Task.Delay(50));
            Check(seek.Value > 4 && player.Position < TimeSpan.FromSeconds(1), "Playback did not cross into the next reordered clip");
            ((Button)editor.FindName("PlayButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            foreach (AppLanguage language in Enum.GetValues<AppLanguage>())
            {
                Loc.Set(language);
                editor.Width = language == AppLanguage.German ? 860 : 1160;
                editor.Height = language == AppLanguage.German ? 640 : 840;
                editor.UpdateLayout();
                Wait(Task.Delay(100));
                var export = (Button)editor.FindName("ExportButton");
                Point top = export.TransformToAncestor(editor).Transform(new Point());
                Check(top.X >= 0 && top.X + export.ActualWidth < editor.ActualWidth && export.IsEnabled, "Export outside editor or disabled");
                SaveWindow(editor, $"editor-{language}.png");
            }
            while (timeline.Undo()) { }
        }
        finally
        {
            var timeline = (VideoEditTimeline?)typeof(VideoEditorWindow).GetField("_timeline", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor);
            while (timeline?.Undo() == true) { }
            editor.Close();
            Loc.Set(AppLanguage.Russian);
        }
    }

    static bool WaitForPreviewColor(MediaElement player, int channel)
    {
        for (int attempt = 0; attempt < 30; attempt++)
        {
            Wait(Task.Delay(50));
            Window window = Window.GetWindow(player);
            int width = Math.Max(1, (int)window.ActualWidth), height = Math.Max(1, (int)window.ActualHeight);
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);
            Point center = player.TransformToAncestor(window).Transform(new Point(player.ActualWidth / 2, player.ActualHeight / 2));
            var pixel = new byte[4];
            bitmap.CopyPixels(new Int32Rect((int)center.X, (int)center.Y, 1, 1), pixel, 4, 0);
            if (pixel[channel] > 150 && pixel.Where((_, index) => index < 3 && index != channel).All(value => value < 80)) return true;
        }
        return false;
    }

    static void EditorHoverChecks()
    {
        var control = new VideoTimelineControl { Timeline = new VideoEditTimeline(TimeSpan.FromSeconds(10)), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        control.UpdateExtent(600, 1);
        var window = new Window { Width = 640, Height = 240, Content = control };
        window.Show();
        try
        {
            window.UpdateLayout();
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            void Hover(Point point) => typeof(VideoTimelineControl).GetMethod("UpdateHover", flags)!.Invoke(control, [point]);
            byte[] Pixels()
            {
                Wait(Task.Delay(30));
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(600, 182, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(control);
                var pixels = new byte[600 * 182 * 4];
                bitmap.CopyPixels(pixels, 600 * 4, 0);
                return pixels;
            }
            Hover(new Point(200, 170));
            byte[] normal = Pixels();
            Hover(new Point(200, 85));
            Check(control.Cursor == Cursors.SizeAll && (string)control.ToolTip == Loc.Get("EditorMoveClip"), "Clip body lacks its move cursor or tooltip");
            byte[] highlighted = Pixels();
            int body = (90 * 600 + 200) * 4;
            Check(!normal.AsSpan(body, 3).SequenceEqual(highlighted.AsSpan(body, 3)), "Hover did not highlight the linked block");
            Hover(new Point(17, 85));
            Check(control.Cursor == Cursors.SizeWE && (string)control.ToolTip == Loc.Get("EditorTrimStart"), "Left handle lacks its trim cursor or tooltip before dragging");
            byte[] handle = Pixels();
            SaveWindow(window, "editor-trim-handle-hover.png");
            int enlarged = (50 * 600 + 20) * 4;
            Check(handle[enlarged] > 220 && handle[enlarged + 1] > 220 && handle[enlarged + 2] > 220, $"Hovered trim handle did not expand and brighten ({handle[enlarged]}/{handle[enlarged + 1]}/{handle[enlarged + 2]})");
            Hover(new Point(582, 85));
            Check(control.Cursor == Cursors.SizeWE && (string)control.ToolTip == Loc.Get("EditorTrimEnd"), "Right handle lacks its trim cursor or tooltip");
            Hover(new Point(200, 15));
            Check(control.Cursor == Cursors.IBeam, "Ruler has a clip movement cursor");
            control.IsEnabled = false;
            Check(control.Cursor == Cursors.Arrow && control.ToolTip == null, "Disabled timeline retained its hover state");
            control.IsEnabled = true;
            Hover(new Point(17, 85));
            SaveWindow(window, "editor-trim-handle-hover.png");
        }
        finally { window.Close(); }
    }

    static void EditorTrimFrameChecks(string source)
    {
        var editor = new VideoEditorWindow(source);
        editor.Show();
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        try
        {
            for (int attempt = 0; attempt < 100 && typeof(VideoEditorWindow).GetField("_mediaReady", flags)!.GetValue(editor) is false; attempt++) Wait(Task.Delay(50));
            Check((bool)typeof(VideoEditorWindow).GetField("_mediaReady", flags)!.GetValue(editor)!, "Frame preview did not open");
            var seek = (Slider)editor.FindName("SeekSlider");
            var player = (MediaElement)editor.FindName("Player");
            var still = (Image)editor.FindName("PausedFrame");
            void Trim(double start, double end)
            {
                ((TextBox)editor.FindName("TrimInBox")).Text = TimeSpan.FromSeconds(start).ToString(@"hh\:mm\:ss\.fff");
                ((TextBox)editor.FindName("TrimOutBox")).Text = TimeSpan.FromSeconds(end).ToString(@"hh\:mm\:ss\.fff");
                typeof(VideoEditorWindow).GetMethod("Trim_Click", flags)!.Invoke(editor, [editor, new RoutedEventArgs()]);
            }
            void SettledFrame(int channel, string message)
            {
                for (int attempt = 0; attempt < 100 && (bool)typeof(VideoEditorWindow).GetField("_previewWorkerRunning", flags)!.GetValue(editor)!; attempt++) Wait(Task.Delay(50));
                Check(!(bool)typeof(VideoEditorWindow).GetField("_previewWorkerRunning", flags)!.GetValue(editor)!, "Paused frame requests did not settle");
                Check(still.Visibility == Visibility.Visible && still.Source != null && WaitForPreviewColor(player, channel), message);
            }
            Trim(.5, 2);
            for (int frame = 0; frame < 9; frame++)
            {
                seek.Value = frame / 30d;
                SettledFrame(2 - (15 + frame) % 3, "Frame stepping after trimming returned the wrong source frame");
            }
            for (int frame = 0; frame < 40; frame++) seek.Value = frame % 12 / 30d;
            seek.Value = 5 / 30d;
            SettledFrame(0, "A stale asynchronous seek replaced the latest selected frame");
            double playhead = seek.Value;
            typeof(VideoEditorWindow).GetMethod("PreviewTrimFrame", flags)!.Invoke(editor,
                [TimeSpan.FromSeconds(3) - TimeSpan.FromTicks(1), TimeSpan.FromSeconds(.5), TimeSpan.FromSeconds(3)]);
            SettledFrame(0, "Trim boundary preview included the excluded out frame");
            Check(seek.Value == playhead, "Dragging a trim edge moved the montage playhead");
            Trim(.5, 3);
            seek.Value = seek.Maximum;
            SettledFrame(0, "Seeking to the trimmed end showed a removed frame");
            Trim(.8, 1.8);
            seek.Value = seek.Maximum;
            SettledFrame(0, "Repeated trimming stopped updating the final retained frame");
            ((Button)editor.FindName("UndoButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            seek.Value = 1 / 30d;
            SettledFrame(1, "Undo failed to restore the trimmed source-time mapping");
            ((Button)editor.FindName("RedoButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            seek.Value = 1 / 30d;
            SettledFrame(1, "Redo failed to restore the trimmed source-time mapping");
            SaveWindow(editor, "editor-trimmed-frame-preview.png");
        }
        finally
        {
            var timeline = (VideoEditTimeline?)typeof(VideoEditorWindow).GetField("_timeline", flags)!.GetValue(editor);
            while (timeline?.Undo() == true) { }
            editor.Close();
        }
    }
}
