using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using FrameForge.Core;
using FrameForge.Capture;
using Microsoft.Win32;

namespace FrameForge.UI;

public partial class SettingsWindow : Window
{
    readonly AppSettings _settings;
    readonly IReadOnlyList<MonitorInfo> _monitors;
    readonly AppLanguage _originalLanguage;
    readonly IReadOnlyList<AudioDeviceInfo> _outputs;
    readonly IReadOnlyList<AudioDeviceInfo> _microphones;
    bool _loading = true;

    SettingsWindow(AppSettings settings, IReadOnlyList<MonitorInfo> monitors)
    {
        InitializeComponent();
        FitAvailableArea(SystemParameters.WorkArea.Width, SystemParameters.WorkArea.Height);
        DpiChanged += (_, _) => FitScreen(this);
        _settings = settings.Clone();
        _monitors = monitors;
        _originalLanguage = Loc.Language;
        _outputs = AudioDeviceCatalog.GetOutputs();
        _microphones = AudioDeviceCatalog.GetMicrophones();
        Loc.Changed += OnLanguageChanged;

        LanguageBox.SelectedValuePath = nameof(LanguageChoice.Value);
        LanguageBox.DisplayMemberPath = nameof(LanguageChoice.Label);
        LanguageBox.ItemsSource = LanguageChoice.All;
        LanguageBox.SelectedValue = _settings.Language ?? Loc.Language;

        MonitorBox.DisplayMemberPath = nameof(MonitorInfo.Label);
        FpsBox.ItemsSource = new[] { 15, 24, 30, 60, 120 };
        FpsBox.SelectedItem = _settings.Fps;
        ResolutionBox.SelectedValuePath = nameof(Option.Value);
        ResolutionBox.DisplayMemberPath = nameof(Option.Label);
        QualityBox.SelectedValuePath = nameof(Option.Value);
        QualityBox.DisplayMemberPath = nameof(Option.Label);
        EncoderBox.SelectedValuePath = nameof(Option.Value);
        EncoderBox.DisplayMemberPath = nameof(Option.Label);
        CursorMotionBox.SelectedValuePath = nameof(Option.Value);
        CursorMotionBox.DisplayMemberPath = nameof(Option.Label);
        CursorSpeedBox.SelectedValuePath = nameof(Option.Value);
        CursorSpeedBox.DisplayMemberPath = nameof(Option.Label);
        SystemAudioDeviceBox.SelectedValuePath = nameof(Option.Value);
        SystemAudioDeviceBox.DisplayMemberPath = nameof(Option.Label);
        MicrophoneDeviceBox.SelectedValuePath = nameof(Option.Value);
        MicrophoneDeviceBox.DisplayMemberPath = nameof(Option.Label);
        FillChoices();
        _loading = false;

        AutoBitrateBox.IsChecked = _settings.AutoBitrate;
        BitrateBox.Text = _settings.ManualBitrateMbps.ToString();
        CursorBox.IsChecked = _settings.CaptureCursor;
        HighlightClicksBox.IsChecked = _settings.HighlightClicks;
        TemporaryMarkerBox.IsChecked = _settings.TemporaryMarker;
        SmoothCursorBox.IsChecked = _settings.SmoothCursor;
        SystemAudioBox.IsChecked = _settings.SystemAudio;
        MicrophoneBox.IsChecked = _settings.Microphone;
        MinimizeBox.IsChecked = _settings.MinimizeOnRecord;
        CheckUpdatesBox.IsChecked = _settings.CheckUpdatesOnStartup;
        FolderBox.Text = string.IsNullOrWhiteSpace(_settings.OutputDirectory)
            ? AppPaths.DefaultOutputDirectory
            : _settings.OutputDirectory;
        StartHotkeyBox.Text = _settings.StartStopHotkey;
        PauseHotkeyBox.Text = _settings.PauseHotkey;
        ShotHotkeyBox.Text = _settings.ScreenshotHotkey;
    }

    public static bool TryEdit(Window owner, AppSettings settings, IReadOnlyList<MonitorInfo> monitors, out AppSettings edited)
    {
        var window = new SettingsWindow(settings, monitors) { Owner = owner };
        window.FitScreen(owner);
        bool accepted = window.ShowDialog() == true;
        edited = window._settings;
        return accepted;
    }

    void Title_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed)
        {
            DragMove();
            FitScreen(this);
        }
    }

    void FitAvailableArea(double width, double height)
    {
        MaxHeight = Math.Max(240, height - 24);
        MaxWidth = Math.Max(280, width - 24);
        MinHeight = Math.Min(340, MaxHeight);
        MinWidth = Math.Min(440, MaxWidth);
        Height = Math.Min(Height, MaxHeight);
        Width = Math.Min(Width, MaxWidth);
    }

    void FitScreen(Window window)
    {
        nint hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == 0) return;
        nint monitor = MonitorFromWindow(hwnd, 2);
        var info = new MonitorWorkArea { Size = Marshal.SizeOf<MonitorWorkArea>() };
        if (!GetMonitorInfo(monitor, ref info)) return;
        double scale = Math.Max(96, GetDpiForWindow(hwnd)) / 96d;
        FitAvailableArea((info.Work.Right - info.Work.Left) / scale, (info.Work.Bottom - info.Work.Top) / scale);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    struct MonitorWorkArea { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfo(nint monitor, ref MonitorWorkArea info);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(nint window);

    void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = Loc.Get("FolderTitle"),
            InitialDirectory = Directory.Exists(FolderBox.Text) ? FolderBox.Text : AppPaths.DefaultOutputDirectory
        };
        if (dialog.ShowDialog(this) == true)
            FolderBox.Text = dialog.FolderName;
    }

    void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    void LanguageBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_loading || LanguageBox.SelectedValue is not AppLanguage language)
            return;
        Loc.Set(language);
    }

    protected override void OnClosed(EventArgs e)
    {
        Loc.Changed -= OnLanguageChanged;
        if (DialogResult != true)
            Loc.Set(_originalLanguage);
        base.OnClosed(e);
    }

    void OnLanguageChanged() => FillChoices();

    void FillChoices()
    {
        object? resolution = ResolutionBox.SelectedValue ?? _settings.Resolution;
        object? quality = QualityBox.SelectedValue ?? _settings.Quality;
        object? encoder = EncoderBox.SelectedValue ?? _settings.Encoder;
        object? motion = CursorMotionBox.SelectedValue ?? _settings.CursorMotion;
        object? speed = CursorSpeedBox.SelectedValue ?? _settings.CursorSmoothnessMilliseconds;
        string outputDevice = (SystemAudioDeviceBox.SelectedValue as string) ?? _settings.SystemAudioDeviceId ?? "";
        string inputDevice = (MicrophoneDeviceBox.SelectedValue as string) ?? _settings.MicrophoneDeviceId ?? "";
        string? monitor = (MonitorBox.SelectedItem as MonitorInfo)?.DeviceName ?? _settings.MonitorDeviceName;

        ResolutionBox.ItemsSource = new[]
        {
            Option.Of(ResolutionPreset.Original, Loc.Get("Original")),
            Option.Of(ResolutionPreset.P1080, "1080p"),
            Option.Of(ResolutionPreset.P1440, "1440p"),
            Option.Of(ResolutionPreset.P4K, "4K")
        };
        QualityBox.ItemsSource = new[]
        {
            Option.Of(VideoQuality.Low, Loc.Get("Low")),
            Option.Of(VideoQuality.Medium, Loc.Get("Medium")),
            Option.Of(VideoQuality.High, Loc.Get("High")),
            Option.Of(VideoQuality.Ultra, Loc.Get("Ultra"))
        };
        EncoderBox.ItemsSource = new[]
        {
            Option.Of(EncoderPreference.Auto, Loc.Get("EncoderAuto")),
            Option.Of(EncoderPreference.Hardware, Loc.Get("EncoderHardware")),
            Option.Of(EncoderPreference.Software, Loc.Get("EncoderSoftware"))
        };
        ResolutionBox.SelectedValue = resolution;
        QualityBox.SelectedValue = quality;
        EncoderBox.SelectedValue = encoder;
        CursorMotionBox.ItemsSource = new[]
        {
            Option.Of(CursorMotionStyle.Curve, Loc.Get("CursorCurve")),
            Option.Of(CursorMotionStyle.Spiral, Loc.Get("CursorSpiral"))
        };
        CursorSpeedBox.ItemsSource = new[]
        {
            Option.Of(400, Loc.Get("CursorSpeedFast")),
            Option.Of(700, Loc.Get("CursorSpeedSmooth")),
            Option.Of(1100, Loc.Get("CursorSpeedSlow"))
        };
        CursorMotionBox.SelectedValue = motion;
        CursorSpeedBox.SelectedValue = speed;
        FillAudioDevices(SystemAudioDeviceBox, _outputs, outputDevice);
        FillAudioDevices(MicrophoneDeviceBox, _microphones, inputDevice);

        MonitorBox.ItemsSource = null;
        MonitorBox.ItemsSource = _monitors;
        MonitorBox.SelectedItem = _monitors.FirstOrDefault(item =>
            string.Equals(item.DeviceName, monitor, StringComparison.OrdinalIgnoreCase))
            ?? _monitors.FirstOrDefault();
    }

    static void FillAudioDevices(System.Windows.Controls.ComboBox box, IReadOnlyList<AudioDeviceInfo> devices, string selected)
    {
        var options = new List<Option> { Option.Of("", Loc.Get("DefaultAudioDevice")) };
        options.AddRange(devices.Select(device => Option.Of(device.Id, device.Name)));
        if (selected.Length > 0 && !devices.Any(device => device.Id == selected))
            options.Add(Option.Of(selected, Loc.Get("MissingAudioDevice")));
        box.ItemsSource = options;
        box.SelectedValue = selected;
    }

    void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!Hotkey.TryParse(StartHotkeyBox.Text, out Hotkey start) ||
            !Hotkey.TryParse(PauseHotkeyBox.Text, out Hotkey pause) ||
            !Hotkey.TryParse(ShotHotkeyBox.Text, out Hotkey shot))
        {
            ErrorText.Text = Loc.Get("HotkeyFormat");
            return;
        }

        if ((start.Modifiers, start.VirtualKey) == (pause.Modifiers, pause.VirtualKey) ||
            (start.Modifiers, start.VirtualKey) == (shot.Modifiers, shot.VirtualKey) ||
            (pause.Modifiers, pause.VirtualKey) == (shot.Modifiers, shot.VirtualKey))
        {
            ErrorText.Text = Loc.Get("HotkeyDuplicate");
            return;
        }

        if (!int.TryParse(BitrateBox.Text, out int mbps) || mbps is < 1 or > 120)
        {
            ErrorText.Text = Loc.Get("BitrateRange");
            return;
        }

        if (MonitorBox.SelectedItem is MonitorInfo monitor)
            _settings.MonitorDeviceName = monitor.DeviceName;
        if (FpsBox.SelectedItem is int fps)
            _settings.Fps = fps;
        if (ResolutionBox.SelectedValue is ResolutionPreset resolution)
            _settings.Resolution = resolution;
        if (QualityBox.SelectedValue is VideoQuality quality)
            _settings.Quality = quality;
        if (EncoderBox.SelectedValue is EncoderPreference encoder)
            _settings.Encoder = encoder;
        _settings.AutoBitrate = AutoBitrateBox.IsChecked == true;
        _settings.ManualBitrateMbps = mbps;
        _settings.CaptureCursor = CursorBox.IsChecked == true;
        _settings.HighlightClicks = HighlightClicksBox.IsChecked == true;
        _settings.TemporaryMarker = TemporaryMarkerBox.IsChecked == true;
        _settings.SmoothCursor = SmoothCursorBox.IsChecked == true;
        _settings.SystemAudio = SystemAudioBox.IsChecked == true;
        _settings.Microphone = MicrophoneBox.IsChecked == true;
        _settings.SystemAudioDeviceId = SystemAudioDeviceBox.SelectedValue as string;
        _settings.MicrophoneDeviceId = MicrophoneDeviceBox.SelectedValue as string;
        if (CursorMotionBox.SelectedValue is CursorMotionStyle motion)
            _settings.CursorMotion = motion;
        if (CursorSpeedBox.SelectedValue is int speed)
            _settings.CursorSmoothnessMilliseconds = speed;
        _settings.MinimizeOnRecord = MinimizeBox.IsChecked == true;
        _settings.CheckUpdatesOnStartup = CheckUpdatesBox.IsChecked == true;
        _settings.OutputDirectory = FolderBox.Text.Trim();
        _settings.StartStopHotkey = StartHotkeyBox.Text.Trim();
        _settings.PauseHotkey = PauseHotkeyBox.Text.Trim();
        _settings.ScreenshotHotkey = ShotHotkeyBox.Text.Trim();
        _settings.Language = Loc.Language;
        _settings.Normalize();
        DialogResult = true;
        Close();
    }

    sealed class Option
    {
        public object Value { get; private init; } = 0;
        public string Label { get; private init; } = "";
        public static Option Of<T>(T value, string label) where T : notnull => new() { Value = value, Label = label };
    }
}
