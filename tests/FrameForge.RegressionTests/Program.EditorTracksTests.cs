using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using FrameForge.Core;
using FrameForge.Capture;
using FrameForge.Encoding;
using FrameForge.UI;
using NAudio.Wave;

internal static partial class Program
{
    static void RunEditorTrackCoreTests()
    {
        RunEditorProjectCoreTests();
        RunEditorWorkspaceCoreTests();
        Test("multiple source bounds, transforms and volumes survive split and history", () =>
        {
            var timeline = new VideoEditTimeline(TimeSpan.FromSeconds(4), "first.mp4");
            timeline.AddSource("second.mp4", TimeSpan.FromSeconds(10), TimeSpan.Zero, VideoEditTrackKind.Video);
            Check(timeline.Trim(1, TimeSpan.FromSeconds(7), TimeSpan.FromSeconds(9)), "Second source used the first source duration");
            var transform = new VideoFrameTransform { Left = .2, QuarterTurns = 1, Scale = .6, OffsetX = .2 };
            timeline.SetSelectedTransform(transform);
            timeline.SetSelectedVolume(.4);
            Check(timeline.Split(TimeSpan.FromSeconds(12)), "Could not split the second source");
            Check(timeline.Clips.Skip(1).All(clip => clip.SourcePath == "second.mp4" && clip.Transform == transform && clip.Volume == .4), "Split lost source or clip settings");
            while (timeline.Undo()) { }
            Check(!timeline.HasChanges && timeline.Clips.Count == 1 && timeline.Duration.TotalSeconds == 4, "History failed to restore the original project");
            Check(!new VideoFrameTransform { Left = double.NaN }.IsValid && !new VideoFrameTransform { Scale = 0 }.IsValid, "Accepted a nonfinite or invisible frame");
        });
        Test("independent tracks preserve timing, prohibit overlaps and share undo history", () =>
        {
            var timeline = new VideoEditTimeline(TimeSpan.FromSeconds(6), "video.mp4");
            Guid track = timeline.AddTrack(VideoEditTrackKind.Audio, "Voice");
            timeline.AddSource("voice.wav", TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(3), VideoEditTrackKind.Audio);
            Check(timeline.Duration.TotalSeconds == 7 && timeline.SelectedStart.TotalSeconds == 3, "Audio tail or delay was lost");
            Check(timeline.Split(TimeSpan.FromSeconds(5)), "Audio split failed");
            Check(timeline.SelectedClip is { Start.TotalSeconds: 2, Position.TotalSeconds: 5, Duration.TotalSeconds: 2 }, "Split did not retain source and timeline offsets");
            Check(!timeline.SetSelectedPosition(TimeSpan.FromSeconds(4)), "Accepted overlapping clips on one track");
            Check(timeline.SetSelectedPosition(TimeSpan.FromSeconds(8)) && timeline.Duration.TotalSeconds == 10, "Could not move the audio tail");
            timeline.SelectedTrackId = null; timeline.SelectedIndex = 0;
            timeline.Trim(0, TimeSpan.Zero, TimeSpan.FromSeconds(1));
            Check(timeline.Tracks[0].Clips[0].Position.TotalSeconds == 3, "Main trim moved independent narration");
            timeline.ToggleMute(track);
            Check(timeline.Tracks[0].Muted && timeline.Undo() && !timeline.Tracks[0].Muted, "Mute is not undoable");
            timeline.SelectedTrackId = track;
            timeline.RemoveSelectedTrack();
            Check(timeline.Tracks.Count == 0 && timeline.Undo() && timeline.Tracks.Count == 1, "Track removal lost history");
        });
    }

    static void RunEditorTrackMediaTests(string source, string silent)
    {
        string wave = Path.Combine(_output, "editor-voice.wav");
        using (var writer = new WaveFileWriter(wave, new WaveFormat(48000, 16, 2)))
        {
            short[] pcm = new short[48000 * 2];
            for (int frame = 0; frame < 48000; frame++) pcm[frame * 2] = pcm[frame * 2 + 1] = (short)(5000 * Math.Sin(frame * 2 * Math.PI * 997 / 48000));
            writer.Write(MemoryMarshal.AsBytes(pcm.AsSpan()));
        }
        Test("multi-source export mixes delayed voiceover and renders layered video", () =>
        {
            var timeline = new VideoEditTimeline(TimeSpan.FromSeconds(6), silent);
            timeline.Trim(0, TimeSpan.Zero, TimeSpan.FromSeconds(1));
            timeline.AddSource(source, TimeSpan.FromSeconds(6), TimeSpan.Zero, VideoEditTrackKind.Video);
            timeline.Trim(1, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3));
            timeline.SetSelectedPosition(TimeSpan.FromSeconds(1));
            timeline.AddTrack(VideoEditTrackKind.Video, "Picture in picture");
            timeline.AddSource(silent, TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(.5), VideoEditTrackKind.Video);
            timeline.Trim(0, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(5));
            timeline.SetSelectedPosition(TimeSpan.FromSeconds(.5));
            timeline.SetSelectedTransform(new() { Scale = .5, OffsetX = .25 });
            timeline.AddTrack(VideoEditTrackKind.Audio, "Voiceover");
            timeline.AddSource(wave, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(.5), VideoEditTrackKind.Audio);
            string destination = Path.Combine(_output, "editor-multitrack.mp4");
            Wait(VideoEditorMedia.ExportAsync(timeline.Snapshot(), destination, 320, 180, 30));
            AudioProbe audio = AudioProbe.Read(destination);
            Check(audio.HasAudio && Math.Abs(audio.Duration100Ns / 1e7 - 2) < .12, "Mixed export has no audio or wrong duration");
            Check(ToneAmplitude(audio.PreviewMono.Take(15000).ToArray(), 997) < .01, "Narration started before its offset");
            Check(ToneAmplitude(audio.PreviewMono.Skip(30000).Take(15000).ToArray(), 997) > .05, "Delayed narration was not mixed");
            Check(ToneAmplitude(audio.PreviewMono.Skip(52000).Take(15000).ToArray(), 880) > .05, "Second video's embedded audio was lost");
            Mp4Probe image = Mp4Probe.Read(destination, 23, maxSamples: 23);
            CheckColor(image, .15, .5, 2, "Main red clip disappeared behind the smaller layer");
            CheckColor(image, .75, .5, 0, "Blue overlay did not retain position, scale or trim");
            byte[] before = File.ReadAllBytes(wave);
            bool protectedSource = false;
            try { Wait(VideoEditorMedia.ExportAsync(timeline.Snapshot(), wave, 320, 180, 30)); }
            catch (InvalidOperationException) { protectedSource = true; }
            Check(protectedSource && before.SequenceEqual(File.ReadAllBytes(wave)), "Export overwrote an imported audio source");
        });
        Test("crop, quarter-turn rotation and frame translation reach encoded pixels", () =>
        {
            string pattern = Path.Combine(_output, "editor-pattern.mp4");
            CreateEditorPattern(pattern);
            var timeline = new VideoEditTimeline(TimeSpan.FromSeconds(1), pattern);
            timeline.SetSelectedTransform(new() { Left = .5 });
            string cropped = Path.Combine(_output, "editor-crop.mp4");
            Wait(VideoEditorMedia.ExportAsync(timeline.Snapshot(), cropped, 320, 180, 30));
            Mp4Probe crop = Mp4Probe.Read(cropped, 5, maxSamples: 5);
            CheckColor(crop, .5, .25, 0, "Crop retained pixels from the discarded left half");
            timeline.SetSelectedTransform(new() { QuarterTurns = 1 });
            string rotated = Path.Combine(_output, "editor-rotate.mp4");
            Wait(VideoEditorMedia.ExportAsync(timeline.Snapshot(), rotated, 320, 180, 30));
            Mp4Probe rotation = Mp4Probe.Read(rotated, 5, maxSamples: 5);
            var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(rotation.PreviewWidth, rotation.PreviewHeight, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, rotation.PreviewPixels!, rotation.PreviewWidth * 4);
            var png = new System.Windows.Media.Imaging.PngBitmapEncoder(); png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using (var output = File.Create(Path.Combine(_output, "rotated-pixels.png"))) png.Save(output);
            CheckColor(rotation, .5, .2, 2, "Clockwise rotation did not move the red half above the blue half");
            CheckColor(rotation, .5, .8, 0, "Clockwise rotation lost the blue lower half");
            timeline.SetSelectedTransform(new() { Scale = .5, OffsetX = .25, OffsetY = .25 });
            string shifted = Path.Combine(_output, "editor-shift.mp4");
            Wait(VideoEditorMedia.ExportAsync(timeline.Snapshot(), shifted, 320, 180, 30));
            Mp4Probe shift = Mp4Probe.Read(shifted, 5, maxSamples: 5);
            CheckColor(shift, .6, .75, 2, "Translation or scale did not reach the final video");
            int pixel = ((int)(shift.PreviewHeight * .25) * shift.PreviewWidth + (int)(shift.PreviewWidth * .25)) * 4;
            Check(shift.PreviewPixels!.AsSpan(pixel, 3).ToArray().All(value => value < 25), "Shift left old pixels outside the moved frame");
        });
        Test("track inspector, layer preview and source switching work in the editor", () => EditorTrackWindowChecks(source, silent, wave));
        Test("opening the editor hides the confirmed capture frame and restores it on close", () => EditorHidesCaptureFrame(source));
        RunEditorProjectMediaTests(source, silent);
        RunEditorWorkspaceUiTests(source);
    }

    static void CreateEditorPattern(string path)
    {
        var pixels = new byte[320 * 180 * 4];
        for (int y = 0; y < 180; y++) for (int x = 0; x < 320; x++)
        {
            int i = (y * 320 + x) * 4;
            pixels[i] = (byte)(x < 160 ? 15 : 240); pixels[i + 1] = 15;
            pixels[i + 2] = (byte)(x < 160 ? 240 : 15); pixels[i + 3] = 255;
        }
        using GraphicsDevice device = GraphicsDevice.CreateForMonitor(new MonitorHandle(0));
        using var writer = Mp4VideoWriter.Open(device.Device, path, 320, 180, 30, 2_000_000, EncoderPreference.Software, out _);
        for (int frame = 0; frame < 30; frame++) writer.WriteBgra(pixels, frame * 10_000_000L / 30, (frame + 1) * 10_000_000L / 30 - frame * 10_000_000L / 30, frame == 0);
        writer.Finish();
    }

    static void CheckColor(Mp4Probe probe, double x, double y, int channel, string message)
    {
        Check(probe.PreviewPixels != null, "No decoded preview pixels");
        int i = ((int)(probe.PreviewHeight * y) * probe.PreviewWidth + (int)(probe.PreviewWidth * x)) * 4;
        Check(probe.PreviewPixels![i + channel] > 150 && probe.PreviewPixels[i + channel] > probe.PreviewPixels[i + (channel + 1) % 3] + 80, message + $" ({probe.PreviewPixels[i]}/{probe.PreviewPixels[i + 1]}/{probe.PreviewPixels[i + 2]})");
    }

    static void EditorTrackWindowChecks(string source, string silent, string wave)
    {
        Loc.Set(AppLanguage.Russian);
        var editor = new VideoEditorWindow(source);
        editor.Show();
        BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        void Invoke(string name, params object[] args) => typeof(VideoEditorWindow).GetMethod(name, flags)!.Invoke(editor, args);
        VideoEditTimeline? timeline = null;
        try
        {
            for (int i = 0; i < 100 && typeof(VideoEditorWindow).GetField("_mediaReady", flags)!.GetValue(editor) is false; i++) Wait(Task.Delay(50));
            timeline = (VideoEditTimeline)typeof(VideoEditorWindow).GetField("_timeline", flags)!.GetValue(editor)!;
            ((TextBox)editor.FindName("CropLeftBox")).Text = "25";
            ((Button)editor.FindName("ApplyFrameButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(timeline.SelectedClip!.Transform.Left == .25, "Frame inspector did not apply crop");
            ((Button)editor.FindName("UndoButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(timeline.SelectedClip!.Transform.Left == 0, "Crop did not undo");
            timeline.Trim(0, TimeSpan.Zero, TimeSpan.FromSeconds(1));
            Wait((Task)typeof(VideoEditorWindow).GetMethod("AddVideoSourceAsync", flags)!.Invoke(editor, [silent])!);
            timeline.Trim(1, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3));
            timeline.SetSelectedPosition(TimeSpan.FromSeconds(1));
            Invoke("AfterEdit", true, null!);
            var player = (MediaElement)editor.FindName("Player");
            Check(WaitForPreviewColor(player, 1), "Appending another file did not switch to its green source interval");
            Invoke("Seek", TimeSpan.FromSeconds(.25));
            Check(WaitForPreviewColor(player, 2), "Seeking back across source files did not restore the first video's red frame");
            timeline.AddTrack(VideoEditTrackKind.Audio, "Озвучка");
            timeline.AddSource(wave, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), VideoEditTrackKind.Audio);
            Invoke("AfterEdit", false, null!);
            Check(!((StackPanel)editor.FindName("FrameTools")).IsEnabled, "Audio clip has video crop tools enabled");
            ((TextBox)editor.FindName("TrackPositionBox")).Text = "00:00:02.000";
            ((Button)editor.FindName("ApplyPositionButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(timeline.SelectedClip!.Position.TotalSeconds == 2, "Inspector did not position audio");
            Invoke("NewVideoTrack_Click", editor, new RoutedEventArgs());
            Check(timeline.SelectedTrack?.Kind == VideoEditTrackKind.Video && timeline.SelectedClips.Count == 0, "New video track was not selected");
            Wait((Task)typeof(VideoEditorWindow).GetMethod("AddVideoSourceAsync", flags)!.Invoke(editor, [silent])!);
            timeline.Trim(0, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(5));
            timeline.SetSelectedPosition(TimeSpan.FromSeconds(2));
            timeline.SetSelectedTransform(new() { Scale = .5, OffsetX = .2 });
            Invoke("AfterEdit", true, null!);
            Wait(Task.Delay(300));
            SaveWindow(editor, "editor-multiple-tracks.png");
        }
        finally { while (timeline?.Undo() == true) { } editor.Close(); }
    }

    static void RunEditorVoiceCaptureChecks(string source)
    {
        Test("voiceover records the microphone at the playhead and stops with playback", () =>
        {
            AppSettings original = SettingsStore.Load();
            AppSettings isolated = original.Clone(); isolated.OutputDirectory = _output; SettingsStore.Save(isolated);
            Loc.Set(AppLanguage.Russian);
            var editor = new VideoEditorWindow(source);
            VideoEditTimeline? timeline = null;
            BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var recorderField = typeof(VideoEditorWindow).GetField("_voiceRecorder", flags)!;
            editor.Show();
            try
            {
                for (int i = 0; i < 100 && typeof(VideoEditorWindow).GetField("_mediaReady", flags)!.GetValue(editor) is false; i++) Wait(Task.Delay(50));
                timeline = (VideoEditTimeline)typeof(VideoEditorWindow).GetField("_timeline", flags)!.GetValue(editor)!;
                var seek = (Slider)editor.FindName("SeekSlider"); seek.Value = .75;
                var voice = (Button)editor.FindName("VoiceButton");
                voice.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(recorderField.GetValue(editor) != null, "Microphone recording did not start: " + ((TextBlock)editor.FindName("StatusText")).Text);
                Check(!((Button)editor.FindName("AddVideoButton")).IsEnabled && !((VideoTimelineControl)editor.FindName("TimelineCanvas")).IsEnabled, "Editing is enabled while the microphone is recording");
                Wait(Task.Delay(800));
                Check(seek.Value > 1.1, "Voice recording did not run alongside video playback");
                voice.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(recorderField.GetValue(editor) == null && timeline.Tracks.Count == 1, "Stopping voiceover did not create a new audio track");
                VideoEditClip clip = timeline.Tracks[0].Clips.Single();
                Check(clip.Position.TotalSeconds == .75 && clip.Duration.TotalSeconds is > .5 and < 1.4 && File.Exists(clip.SourcePath), "Voice track lost its start, duration or WAV source");
                Check(AudioProbe.Read(clip.SourcePath!).HasAudio, "Microphone WAV is not decodable");
                seek.Value = timeline.Duration.TotalSeconds - .35;
                voice.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                for (int i = 0; i < 60 && recorderField.GetValue(editor) != null; i++) Wait(Task.Delay(50));
                Check(recorderField.GetValue(editor) == null && timeline.Tracks.Count == 2, "End of video did not stop and retain the voice recording");
                SaveWindow(editor, "editor-recorded-voiceover.png");
            }
            finally
            {
                typeof(VideoEditorWindow).GetMethod("Pause", flags)!.Invoke(editor, null);
                while (timeline?.Undo() == true) { }
                editor.Close(); SettingsStore.Save(original);
            }
        });
    }

    static void EditorHidesCaptureFrame(string source)
    {
        BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        AppSettings original = SettingsStore.Load();
        SettingsStore.Save(new AppSettings { Mode = CaptureMode.Region, RegionX = 200, RegionY = 200, RegionWidth = 640, RegionHeight = 360, Language = AppLanguage.Russian, OutputDirectory = _output });
        Window? previous = Application.Current.MainWindow;
        var owner = new Window { Width = 300, Height = 200, Title = "FrameForge editor frame check" };
        Application.Current.MainWindow = owner; owner.Show();
        var model = new MainViewModel();
        object border = typeof(MainViewModel).GetField("_regionBorder", flags)!.GetValue(model)!;
        var handle = (Window)border.GetType().GetField("_handle", flags)!.GetValue(border)!;
        nint halo = (nint)border.GetType().GetField("_halo", flags)!.GetValue(border)!;
        try
        {
            Check(handle.IsVisible && EditorIsWindowVisible(halo), "Capture frame was not initially visible");
            typeof(MainViewModel).GetField("_lastVideoFile", flags)!.SetValue(model, source);
            model.EditVideoCommand.Execute(null);
            VideoEditorWindow? editor = Application.Current.Windows.OfType<VideoEditorWindow>().SingleOrDefault();
            Check(editor != null && !handle.IsVisible && !EditorIsWindowVisible(halo), "Editor left the capture frame or toolbar visible");
            typeof(MainViewModel).GetMethod("SyncRegionBorder", flags)!.Invoke(model, null);
            Check(!handle.IsVisible && !EditorIsWindowVisible(halo), "A region refresh redisplayed the frame while the editor was open");
            editor!.Close();
            Check(handle.IsVisible && EditorIsWindowVisible(halo), "Closing the editor did not restore the capture frame");
        }
        finally
        {
            Wait(model.CloseEditorAsync()); Wait(model.ShutdownAsync()); owner.Close(); Application.Current.MainWindow = previous;
            SettingsStore.Save(original);
        }
    }

    [DllImport("user32.dll", EntryPoint = "IsWindowVisible")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool EditorIsWindowVisible(nint window);
}
