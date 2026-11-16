using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using FrameForge.Core;

namespace FrameForge.UI;

public partial class VideoEditorWindow
{
    sealed record OutputFormat(string Label, uint Width, uint Height);
    TimeSpan? _workingFadeIn, _workingFadeOut;

    void EditorLanguageChanged()
    {
        if (_closed) return;
        ConfigureOutputFormats(); RefreshTracks(); RefreshInspector(); RefreshControls();
        if (MicrophoneBox.ItemsSource is IEnumerable<FrameForge.Capture.AudioDeviceInfo> microphones)
        {
            string? selected = MicrophoneBox.SelectedValue as string;
            var updated = microphones.Select(device => device.Id.Length == 0 ? device with { Name = Loc.Get("EditorDefaultMicrophone") } : device).ToArray();
            MicrophoneBox.ItemsSource = updated; MicrophoneBox.SelectedValue = selected;
        }
    }

    void ConfigureOutputFormats()
    {
        if (_source == null) return;
        bool refreshing = _refreshingInspector; _refreshingInspector = true;
        try
        {
            VideoEditorSource original = _previews.Values.FirstOrDefault()?.Source ?? _source;
            var formats = new List<OutputFormat>
            {
                new($"{Loc.Get("EditorFormatOriginal")} · {original.Width} × {original.Height}", original.Width, original.Height),
                new($"{Loc.Get("EditorFormatLandscape")} · 1080p", 1920, 1080),
                new($"{Loc.Get("EditorFormatPortrait")} · 1080p", 1080, 1920),
                new($"{Loc.Get("EditorFormatSquare")} · 1080", 1080, 1080),
                new($"{Loc.Get("EditorFormatLandscape")} · 720p", 1280, 720),
                new($"{Loc.Get("EditorFormatPortrait")} · 720p", 720, 1280)
            };
            OutputFormat? selected = formats.FirstOrDefault(format => format.Width == _source.Width && format.Height == _source.Height);
            if (selected == null) { selected = new($"{_source.Width} × {_source.Height}", _source.Width, _source.Height); formats.Add(selected); }
            OutputFormatBox.ItemsSource = formats; OutputFormatBox.SelectedItem = selected;
        }
        finally { _refreshingInspector = refreshing; }
    }

    void OutputFormat_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshingInspector || _source == null || OutputFormatBox.SelectedItem is not OutputFormat format ||
            format.Width == _source.Width && format.Height == _source.Height) return;
        Pause(); _source = _source with { Width = format.Width, Height = format.Height };
        _projectNeedsSave = true;
        _frameMode = "move"; FinishCropButton.Visibility = Visibility.Collapsed;
        FileLabel.Text = $"{Path.GetFileName(_projectPath ?? _sourcePath)}  ·  {format.Width} × {format.Height}  ·  {_source.FrameRate:0.##} FPS";
        UpdatePreviewLayout(); RefreshProjectTitle();
    }

    void FillFrame_Click(object sender, RoutedEventArgs e)
    {
        if (_source == null || _timeline?.SelectedClip is not { } clip || !TryGetVisualSource(clip, out VideoEditorSource source)) return;
        VideoFrameTransform transform = clip.Transform;
        double width = source.Width * (1 - transform.Left - transform.Right), height = source.Height * (1 - transform.Top - transform.Bottom);
        if (transform.QuarterTurns % 2 == 1) (width, height) = (height, width);
        double scale = Math.Max(_source.Width / width, _source.Height / height) / VideoEditorVisuals.Fit(clip, _source.Width, _source.Height, width, height);
        if (_timeline.SetSelectedTransform(transform with { Scale = Math.Clamp(scale, .1, 4), OffsetX = 0, OffsetY = 0 })) AfterEdit();
    }

    void InitializeFadeSliders()
    {
        foreach (Slider slider in new[] { FadeInSlider, FadeOutSlider })
        {
            slider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) => CommitFades()));
            slider.PreviewMouseLeftButtonUp += (_, _) => CommitFades();
            slider.PreviewKeyUp += (_, _) => CommitFades();
        }
    }

    void RefreshFades(VideoEditClip? clip)
    {
        _workingFadeIn = _workingFadeOut = null;
        FadeInSlider.Maximum = Math.Min(5, Math.Max(0, ((clip?.Duration ?? TimeSpan.Zero) - (clip?.FadeOut ?? TimeSpan.Zero)).TotalSeconds));
        FadeOutSlider.Maximum = Math.Min(5, Math.Max(0, ((clip?.Duration ?? TimeSpan.Zero) - (clip?.FadeIn ?? TimeSpan.Zero)).TotalSeconds));
        FadeInSlider.Value = clip?.FadeIn.TotalSeconds ?? 0; FadeOutSlider.Value = clip?.FadeOut.TotalSeconds ?? 0;
        FadeInLabel.Text = $"{FadeInSlider.Value:0.0} s"; FadeOutLabel.Text = $"{FadeOutSlider.Value:0.0} s";
    }

    void Fade_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_refreshingInspector || _timeline?.SelectedClip == null || FadeInSlider == null || FadeOutSlider == null) return;
        Pause(); _workingFadeIn = TimeSpan.FromSeconds(FadeInSlider.Value); _workingFadeOut = TimeSpan.FromSeconds(FadeOutSlider.Value);
        FadeInLabel.Text = $"{FadeInSlider.Value:0.0} s"; FadeOutLabel.Text = $"{FadeOutSlider.Value:0.0} s";
        UpdatePreviewLayout();
    }

    void CommitFades()
    {
        if (_refreshingInspector || _workingFadeIn == null || _workingFadeOut == null) return;
        TimeSpan fadeIn = _workingFadeIn.Value, fadeOut = _workingFadeOut.Value;
        _workingFadeIn = _workingFadeOut = null;
        if (_timeline?.SetSelectedFades(fadeIn, fadeOut) == true) AfterEdit();
    }

    double PreviewOpacity(VideoEditClip clip, TimeSpan localTime)
    {
        if (_timeline?.SelectedClip?.Id == clip.Id && _workingFadeIn != null && _workingFadeOut != null)
            clip = clip with { FadeIn = _workingFadeIn.Value, FadeOut = _workingFadeOut.Value };
        return _frameMode == "crop" && _timeline?.SelectedClip?.Id == clip.Id ? 1 : clip.OpacityAt(localTime);
    }
}
