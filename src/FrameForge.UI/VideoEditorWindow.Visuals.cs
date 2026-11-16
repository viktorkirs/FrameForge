using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FrameForge.Core;
using FrameForge.Diagnostics;
using Microsoft.Win32;

namespace FrameForge.UI;

public partial class VideoEditorWindow
{
    readonly Dictionary<string, BitmapSource> _imageBitmaps = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<Guid, (VideoEditText Text, BitmapSource Bitmap)> _textBitmaps = new();
    static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff"];
    static readonly string[] VideoExtensions = [".mp4", ".mov", ".m4v", ".wmv", ".avi"];
    static readonly string[] AudioExtensions = [".wav", ".mp3", ".flac", ".m4a"];

    void InitializeVisualLayers()
    {
        _refreshingInspector = true;
        TextFontBox.ItemsSource = Fonts.SystemFontFamilies.Select(font => font.Source).Where(name => name.Length <= 128).Order(StringComparer.CurrentCultureIgnoreCase).ToArray();
        TextFontBox.SelectedItem = "Segoe UI"; TextAlignmentBox.SelectedIndex = 1;
        _refreshingInspector = false;
    }

    void ClearVisualAssets()
    {
        _imageBitmaps.Clear(); _textBitmaps.Clear(); TimelineCanvas.VisualThumbnails.Clear();
    }

    BitmapSource GetStillBitmap(VideoEditClip clip)
    {
        BitmapSource bitmap;
        if (clip.Kind == VideoEditClipKind.Text)
        {
            if (!_textBitmaps.TryGetValue(clip.Id, out var known) || known.Text != clip.Text)
                _textBitmaps[clip.Id] = known = (clip.Text!, VideoEditorVisuals.RenderText(clip.Text!));
            bitmap = known.Bitmap;
        }
        else
        {
            string path = Path.GetFullPath(clip.SourcePath!);
            if (!_imageBitmaps.TryGetValue(path, out bitmap!)) _imageBitmaps[path] = bitmap = VideoEditorVisuals.ReadImage(path);
        }
        TimelineCanvas.VisualThumbnails[clip.Id] = bitmap;
        return bitmap;
    }

    bool TryGetVisualSource(VideoEditClip clip, out VideoEditorSource source)
    {
        if (clip.IsStill)
        {
            BitmapSource bitmap = GetStillBitmap(clip);
            source = new(clip.OriginalDuration, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 30, false); return true;
        }
        if (_previews.TryGetValue(clip.SourcePath ?? _sourcePath!, out VideoEditorPreview? preview)) { source = preview.Source; return true; }
        source = null!; return false;
    }

    void EnsureVisualProject()
    {
        if (_timeline != null) return;
        Player.Close(); CloseTrackPlayback(); ClearVisualAssets();
        _previews.Clear(); _playerPath = _sourcePath = null; _framePreview = null; _mediaReady = false;
        _timeline = new VideoEditTimeline(TimeSpan.FromSeconds(5)); _timeline.DeleteSelected();
        _source = new(TimeSpan.FromSeconds(5), 1920, 1080, 30, false);
        _waveformCancellation?.Cancel(); _waveformCancellation?.Dispose();
        _waveformCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _projectPath = null; _savedProjectState = _timeline.StateId; _projectNeedsSave = false;
        TimelineCanvas.Timeline = _timeline; TimelineCanvas.SourceThumbnails.Clear(); TimelineCanvas.Waveforms.Clear();
        TimelineCanvas.Waveform = null; _thumbnails = new(); TimelineCanvas.Thumbnails = _thumbnails;
        _position = TimeSpan.Zero; ZoomSlider.Value = 1; ConfigureOutputFormats();
        FileLabel.Text = $"{Loc.Get("EditorNewProject")} · 1920 × 1080 · 30 FPS";
    }

    void ShowSelectedTrack()
    {
        if (_timeline == null) return;
        TimelineVertical.UpdateLayout();
        int index = _timeline.Tracks.ToList().FindIndex(track => track.Id == _timeline.SelectedTrackId);
        double top = index < 0 ? 0 : TimelineCanvas.LaneY(176 + index * 64);
        double bottom = TimelineCanvas.LaneY(index < 0 ? 164 : 240 + index * 64);
        if (top < TimelineVertical.VerticalOffset) TimelineVertical.ScrollToVerticalOffset(top);
        else if (bottom > TimelineVertical.VerticalOffset + TimelineVertical.ViewportHeight)
            TimelineVertical.ScrollToVerticalOffset(bottom - TimelineVertical.ViewportHeight);
    }

    void AddImageSource(string path, TimeSpan position)
    {
        path = Path.GetFullPath(path);
        BitmapSource bitmap = VideoEditorVisuals.ReadImage(path);
        EnsureVisualProject(); _imageBitmaps[path] = bitmap;
        if (_timeline!.AddImageLayer(path, TimeSpan.FromSeconds(5), position, Loc.Get("EditorImageLayer") + " · " + Path.GetFileName(path)) == null)
            throw new InvalidOperationException(Loc.Get("EditorLayerLimit"));
    }

    void AddImage_Click(object sender, RoutedEventArgs e)
    {
        if (_loading || _exporting || _voiceRecorder != null) return;
        Pause();
        var dialog = new OpenFileDialog { Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff", Multiselect = true, Title = Loc.Get("EditorAddImage") };
        if (dialog.ShowDialog(this) != true) return;
        try { foreach (string path in dialog.FileNames) AddImageSource(path, _position); AfterEdit(selectStart: true); }
        catch (Exception ex) { if (_timeline != null) AfterEdit(selectStart: true); StatusText.Text = ex.Message; Log.Error("Importing editor image.", ex); RefreshControls(); }
    }

    void AddText_Click(object sender, RoutedEventArgs e)
    {
        if (_loading || _exporting || _voiceRecorder != null) return;
        Pause(); EnsureVisualProject();
        var text = new VideoEditText { Content = Loc.Get("EditorDefaultText"), ReferenceWidth = _source!.Width, FontSize = Math.Clamp(_source.Width * .05, 12, 128) };
        if (_timeline!.AddTextLayer(text, TimeSpan.FromSeconds(5), _position, Loc.Get("EditorTextLayer")) == null)
        { StatusText.Text = Loc.Get("EditorLayerLimit"); return; }
        AfterEdit(selectStart: true); TextContentBox.Focus(); TextContentBox.SelectAll();
    }

    void RefreshTextInspector(VideoEditClip? clip)
    {
        TextTools.Visibility = clip?.Kind == VideoEditClipKind.Text ? Visibility.Visible : Visibility.Collapsed;
        StillTools.Visibility = clip?.IsStill == true ? Visibility.Visible : Visibility.Collapsed;
        if (clip?.IsStill == true) LayerDurationBox.Text = clip.Duration.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        if (clip?.Text is not { } text) return;
        TextContentBox.Text = text.Content; TextFontBox.SelectedItem = text.FontFamily;
        if (TextFontBox.SelectedItem == null) { TextFontBox.ItemsSource = ((IEnumerable<string>)TextFontBox.ItemsSource).Append(text.FontFamily).Distinct().Order().ToArray(); TextFontBox.SelectedItem = text.FontFamily; }
        TextSizeSlider.Value = text.FontSize; TextSizeBox.Text = text.FontSize.ToString("0.#", CultureInfo.InvariantCulture);
        TextColorBox.Text = text.Color; TextColorPreview.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(text.Color));
        RefreshColorChannels(text.Color);
        TextBoldBox.IsChecked = text.Bold; TextItalicBox.IsChecked = text.Italic; TextUnderlineBox.IsChecked = text.Underline;
        TextAlignmentBox.SelectedIndex = (int)text.Alignment;
    }

    void TextLayer_Changed(object sender, RoutedEventArgs e)
    {
        if (_refreshingInspector || _timeline?.SelectedClip is not { Kind: VideoEditClipKind.Text, Text: { } original } clip || TextAlignmentBox == null) return;
        if (!double.TryParse(TextSizeBox.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double size)) return;
        string color = TextColorBox.Text.Trim(); if (color.Length == 7 && color[0] == '#') color = "#FF" + color[1..];
        var text = original with
        {
            Content = TextContentBox.Text, FontFamily = TextFontBox.SelectedItem as string ?? original.FontFamily,
            FontSize = size, Color = color.ToUpperInvariant(), Bold = TextBoldBox.IsChecked == true,
            Italic = TextItalicBox.IsChecked == true, Underline = TextUnderlineBox.IsChecked == true,
            Alignment = (VideoTextAlignment)Math.Max(0, TextAlignmentBox.SelectedIndex)
        };
        if (!text.IsValid) { StatusText.Text = Loc.Get("EditorTextInvalid"); return; }
        if (text == original) return;
        try
        {
            BitmapSource bitmap = VideoEditorVisuals.RenderText(text);
            if (_playing) Pause();
            if (_timeline.SetSelectedText(text))
            {
                _textBitmaps[clip.Id] = (text, bitmap); TimelineCanvas.VisualThumbnails[clip.Id] = bitmap;
                _refreshingInspector = true; TextSizeSlider.Value = size; _refreshingInspector = false;
                TextColorPreview.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(text.Color));
                _refreshingInspector = true; RefreshColorChannels(text.Color); _refreshingInspector = false;
                SelectedClipLabel.Text = VideoEditorVisuals.Label(_timeline.SelectedClip!);
                SyncTrackPlayback(force: true); UpdatePreviewLayout(); TimelineCanvas.InvalidateVisual(); RefreshProjectTitle(); StatusText.Text = "";
            }
        }
        catch (Exception ex) { StatusText.Text = ex.Message; Log.Error("Formatting editor text.", ex); }
        finally { _refreshingInspector = false; }
    }

    void TextSize_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_refreshingInspector || TextSizeBox == null) return;
        TextSizeBox.Text = e.NewValue.ToString("0.#", CultureInfo.InvariantCulture);
    }

    void TextColor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string color }) TextColorBox.Text = color;
    }

    void RefreshColorChannels(string value)
    {
        Color color = (Color)ColorConverter.ConvertFromString(value);
        TextRedSlider.Value = color.R; TextGreenSlider.Value = color.G; TextBlueSlider.Value = color.B; TextAlphaSlider.Value = color.A;
    }

    void TextColorChannel_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_refreshingInspector || TextAlphaSlider == null || _timeline?.SelectedClip?.Kind != VideoEditClipKind.Text) return;
        TextColorBox.Text = $"#{(byte)TextAlphaSlider.Value:X2}{(byte)TextRedSlider.Value:X2}{(byte)TextGreenSlider.Value:X2}{(byte)TextBlueSlider.Value:X2}";
    }

    void ApplyLayerDuration_Click(object sender, RoutedEventArgs e)
    {
        if (_timeline?.SelectedClip is not { IsStill: true } clip ||
            !double.TryParse(LayerDurationBox.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) ||
            !double.IsFinite(seconds) || seconds < VideoEditTimeline.MinimumDuration.TotalSeconds || seconds > (TimeSpan.FromDays(7) - clip.Position).TotalSeconds)
        { StatusText.Text = Loc.Get("EditorLayerDurationInvalid"); return; }
        Pause(); if (_timeline.Trim(_timeline.SelectedIndex, clip.Start, clip.Start + TimeSpan.FromSeconds(seconds))) AfterEdit();
    }

    static bool SupportedDrop(string path) => ImageExtensions.Concat(VideoExtensions).Concat(AudioExtensions).Append(VideoEditProjectFile.Extension).Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    void Editor_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        e.Effects = !_loading && !_exporting && _voiceRecorder == null && e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 and <= 128 } paths
            && paths.All(SupportedDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    async void Editor_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        e.Handled = true;
        if (_loading || _exporting || _voiceRecorder != null || e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        Point point = e.GetPosition(TimelineCanvas);
        Point viewport = e.GetPosition(TimelineVertical);
        TimeSpan at = new Rect(0, 0, TimelineVertical.ActualWidth, TimelineVertical.ActualHeight).Contains(viewport) ? TimelineCanvas.DropTimeAt(point.X) : _position;
        await ImportDroppedFilesAsync(paths, at);
    }

    async Task ImportDroppedFilesAsync(string[] paths, TimeSpan at)
    {
        if (_loading || _exporting || _voiceRecorder != null || paths.Length is 0 or > 128 || !paths.All(SupportedDrop)) return;
        if (paths.Any(path => string.Equals(Path.GetExtension(path), VideoEditProjectFile.Extension, StringComparison.OrdinalIgnoreCase)))
        { if (paths.Length == 1) OpenDocument(paths[0]); else StatusText.Text = Loc.Get("EditorDropProjectAlone"); return; }
        Pause(); _loading = true; RefreshControls();
        try
        {
            foreach (string input in paths)
            {
                string path = Path.GetFullPath(input), extension = Path.GetExtension(path).ToLowerInvariant();
                if (ImageExtensions.Contains(extension)) AddImageSource(path, at);
                else if (VideoExtensions.Contains(extension))
                {
                    VideoEditorPreview preview = await VideoEditorMedia.OpenPreviewAsync(path); if (_closed) return;
                    bool newProject = _timeline == null;
                    EnsureVisualProject(); _previews[path] = preview; NewTrack(VideoEditTrackKind.Video);
                    if (newProject)
                    {
                        _source = preview.Source; ConfigureOutputFormats();
                        FileLabel.Text = $"{Loc.Get("EditorNewProject")} · {_source.Width} × {_source.Height} · {_source.FrameRate:0.##} FPS";
                    }
                    _timeline!.AddSource(path, preview.Source.Duration, at, VideoEditTrackKind.Video);
                    _ = ReadAdditionalWaveformAsync(path, preview.Source.Duration);
                }
                else
                {
                    TimeSpan duration = await VideoEditorMedia.ReadAudioDurationAsync(path); if (_closed) return;
                    EnsureVisualProject(); NewTrack(VideoEditTrackKind.Audio);
                    _timeline!.AddSource(path, duration, at, VideoEditTrackKind.Audio); _ = ReadAdditionalWaveformAsync(path, duration);
                }
            }
            AfterEdit(selectStart: true);
        }
        catch (Exception ex) { if (_timeline != null) AfterEdit(selectStart: true); StatusText.Text = ex.Message; Log.Error("Dropping editor media.", ex); }
        finally { _loading = false; if (!_closed) { RefreshTracks(); RefreshControls(); } }
    }
}
