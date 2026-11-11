using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FrameForge.Capture;
using FrameForge.Core;
using FrameForge.Diagnostics;
using FrameForge.Encoding;

namespace FrameForge.UI;

public sealed class MainViewModel : ObservableObject
{
    readonly MarkerState _markerState = new();
    readonly RecordingEngine _engine;
    readonly MarkerController _markerController;
    readonly RegionBorder _regionBorder = new();
    readonly DispatcherTimer _timer;
    AppSettings _settings;
    IReadOnlyList<MonitorInfo> _monitors = [];
    MonitorInfo? _monitor;
    MonitorInfo? _recordingMonitor;
    nint _window;
    string _windowTitle = "";
    string _windowProcess = "";
    int _regionX;
    int _regionY;
    int _regionWidth;
    int _regionHeight;
    bool _busy;
    bool _shuttingDown;
    Task _recordingTask = Task.CompletedTask;
    bool _finishing;
    bool _finishRequested;
    int _diskBusy;
    DateTime _nextDiskUtc;
    long _shownFree = -1;
    string _shownDrive = "";
    int _shownBitrate;
    string? _riskKey;
    string _diskEstimate = "";
    bool _diskEstimateLow;
    string _machineWarning = "";

    public MainViewModel()
    {
        _engine = new RecordingEngine(_markerState);
        _markerController = new MarkerController(_markerState);
        _settings = SettingsStore.Load();
        Loc.Set(_settings.Language ?? Loc.FromCulture());
        Loc.Changed += OnLanguageChanged;
        RecordCommand = new RelayCommand(ToggleRecord, () => !_busy && !_finishing);
        PauseCommand = new RelayCommand(TogglePause, () => IsRecording && !_busy && !_finishing);
        ScreenshotCommand = new RelayCommand(TakeScreenshot, () => !_busy && !_finishing);
        SettingsCommand = new RelayCommand(EditSettings, () => !IsRecording && !_busy);
        AboutCommand = new RelayCommand(() => new AboutWindow { Owner = Application.Current.MainWindow }.ShowDialog());
        CycleMonitorCommand = new RelayCommand(CycleMonitor, () => !IsRecording && !_busy);
        ScreenModeCommand = new RelayCommand(SelectScreen, () => !IsRecording && !_busy);
        WindowModeCommand = new RelayCommand(SelectWindow, () => !IsRecording && !_busy);
        RegionModeCommand = new RelayCommand(SelectRegion, () => !IsRecording && !_busy);
        OpenFolderCommand = new RelayCommand(OpenSavedFolder, () => CanOpenFolder);
        _regionX = _settings.RegionX;
        _regionY = _settings.RegionY;
        _regionWidth = _settings.RegionWidth;
        _regionHeight = _settings.RegionHeight;
        _regionBorder.MoveRequested = TryMoveRegion;
        _regionBorder.EditRequested += () => RegionModeCommand.Execute(null);
        _regionBorder.MoveCompleted += () =>
        {
            if (_shuttingDown)
                return;
            SettingsStore.Save(_settings);
            RefreshTarget();
        };
        _engine.PreviewReady += OnPreview;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _timer.Tick += (_, _) => PullSnapshot();
        _timer.Start();
        ReloadMonitors();
        if (_settings.Mode == CaptureMode.Window)
            Show("WindowHint");
        else if (_settings.Mode == CaptureMode.Region && _regionWidth >= 16)
            Show("TargetRegion", _regionWidth, _regionHeight);
        else if (_settings.Mode == CaptureMode.Region)
            Show("RegionHint");
        SyncRegionBorder();
        ProbeHardware();
        PublishDiskEstimate();
        _nextDiskUtc = DateTime.UtcNow.AddSeconds(3);
        ConfigureMarker();
    }

    public RelayCommand RecordCommand { get; }
    public RelayCommand PauseCommand { get; }
    public RelayCommand ScreenshotCommand { get; }
    public RelayCommand SettingsCommand { get; }
    public RelayCommand AboutCommand { get; }
    public RelayCommand CycleMonitorCommand { get; }
    public RelayCommand ScreenModeCommand { get; }
    public RelayCommand WindowModeCommand { get; }
    public RelayCommand RegionModeCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public string ScreenTag => _settings.Mode == CaptureMode.Monitor ? "Selected" : "";
    public string WindowTag => _settings.Mode == CaptureMode.Window ? "Selected" : "";
    public string RegionTag => _settings.Mode == CaptureMode.Region ? "Selected" : "";
    public string TargetText => _settings.Mode switch
    {
        CaptureMode.Window => _window != 0 && WindowCatalog.IsAlive(_window)
            ? Loc.Format("TargetWindow", WindowLabel)
            : Loc.Get("WindowHint"),
        CaptureMode.Region => _regionWidth >= 16 && _regionHeight >= 16
            ? Loc.Format("TargetRegion", _regionWidth, _regionHeight)
            : Loc.Get("RegionHint"),
        _ => Loc.Get("TargetScreen")
    };
    string WindowLabel => string.IsNullOrWhiteSpace(_windowProcess) ? _windowTitle : $"{_windowTitle} — {_windowProcess}";
    public ImageSource? PreviewImage { get; private set; }
    public string MonitorLabel => _monitor?.Label ?? Loc.Get("MonitorMissing");
    public string ResolutionText => IsRecording && _engine.Snapshot.Width > 0
        ? $"{_engine.Snapshot.Width} × {_engine.Snapshot.Height}"
        : _monitor?.ResolutionText ?? "—";
    public string QualityText => _settings.QualityLine;
    string _hint = "";
    public string Hint
    {
        get => _hint;
        private set
        {
            if (_hint == value)
                return;
            _hint = value;
            OnPropertyChanged();
        }
    }
    public string TimerText { get; private set; } = "00:00:00";
    public string StatsText { get; private set; } = "";
    public string EncoderText { get; private set; } = "";
    public bool IsRecording { get; private set; }
    public bool IsPaused { get; private set; }
    public bool IsFinishing => _finishing;
    public string DiskEstimate => _diskEstimate;
    public bool DiskEstimateLow => _diskEstimateLow;
    public bool HasDiskEstimate => _diskEstimate.Length > 0;
    public string MachineWarning => _machineWarning;
    public bool HasMachineWarning => _machineWarning.Length > 0;
    // Media Foundation does not report finalize progress, so this bar stays indeterminate.
    public bool ShowElapsed => IsRecording || IsPaused || _finishing;
    string? _savedFile;
    public bool CanOpenFolder => _savedFile != null && File.Exists(_savedFile);
    public bool ShowPlaceholder => PreviewImage == null;
    public string RecordText => IsRecording ? Loc.Get("Stop") : Loc.Get("Record");
    public string PauseText => IsPaused ? Loc.Get("Resume") : Loc.Get("Pause");
    public double SystemAudioLevel { get; private set; }
    public double MicrophoneLevel { get; private set; }
    public bool SystemAudioActive => SystemAudio && IsRecording && !IsPaused && !_finishing;
    public bool MicrophoneActive => Microphone && IsRecording && !IsPaused && !_finishing;
    public bool SystemAudio
    {
        get => _settings.SystemAudio;
        set
        {
            _settings.SystemAudio = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(AudioNote));
            OnPropertyChanged(nameof(HasAudioNote));
            NotifyAudioLevels();
            SettingsStore.Save(_settings);
        }
    }
    public bool Microphone
    {
        get => _settings.Microphone;
        set
        {
            _settings.Microphone = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(AudioNote));
            OnPropertyChanged(nameof(HasAudioNote));
            NotifyAudioLevels();
            SettingsStore.Save(_settings);
        }
    }
    public string AudioNote => SystemAudio && Microphone ? Loc.Get("AudioMixedNote")
        : SystemAudio ? Loc.Get("AudioNote") : Microphone ? Loc.Get("MicrophoneNote") : "";
    public bool HasAudioNote => AudioNote.Length > 0;
    public AppSettings Settings => _settings;
    public bool MinimizeOnRecord => _settings.MinimizeOnRecord;

    public void RequestRecord() => RecordCommand.Execute(null);
    public void RequestPause() => PauseCommand.Execute(null);
    public void RequestScreenshot() => ScreenshotCommand.Execute(null);
    string? _hintKey;
    object?[] _hintArgs = [];

    public void Show(string key, params object?[] args)
    {
        if (key != "Saved")
            ClearSavedFile();
        _hintKey = key;
        _hintArgs = args;
        Hint = args.Length == 0 ? Loc.Get(key) : Loc.Format(key, args);
    }

    public void SetHint(string text)
    {
        _hintKey = null;
        ClearSavedFile();
        Hint = text;
    }

    public event Action? RecordingStarted;
    public event Action? RecordingStopped;
    public event Action? SettingsChanged;

    public async Task ShutdownAsync()
    {
        _shuttingDown = true;
        _markerController.Dispose();
        SetBusy(true);
        _regionBorder.Dispose();
        _timer.Stop();
        Loc.Changed -= OnLanguageChanged;
        _engine.PreviewReady -= OnPreview;
        await _recordingTask;
        await Task.Run(_engine.Dispose);
        SettingsStore.Save(_settings);
    }

    void ToggleRecord() => _recordingTask = ToggleRecordAsync();

    async Task ToggleRecordAsync()
    {
        if (_finishRequested || _finishing)
            return;
        if (IsRecording)
        {
            await StopRecordingAsync();
            return;
        }

        if (_monitor == null)
        {
            Show("NoMonitor");
            return;
        }

        if (!TryCreateRequest(out CaptureRequest request, out string errorKey))
        {
            Show(errorKey);
            return;
        }

        SetBusy(true);
        Show("Preparing");
        try
        {
            string directory = AppPaths.ResolveOutputDirectory(_settings.OutputDirectory);
            string path = AppPaths.CreateRecordingPath(directory);
            _settings.OutputDirectory = directory;
            SettingsStore.Save(_settings);
            AppSettings snapshot = _settings.Clone();
            _recordingMonitor = request.Monitor;
            await Task.Run(() => _engine.Start(request, path, snapshot));
            if (_shuttingDown)
                return;
            IsRecording = true;
            IsPaused = false;
            SetHint(_engine.Snapshot.Message ?? "");
            NotifyRecording();
            RecordingStarted?.Invoke();
            PullSnapshot();
        }
        catch (Exception ex)
        {
            Log.Error("Не удалось начать запись.", ex);
            SetHint(ex.Message);
            IsRecording = false;
            IsPaused = false;
            NotifyRecording();
        }
        finally
        {
            SetBusy(false);
        }
    }

    async void TogglePause()
    {
        if (!IsRecording || _finishing)
            return;
        if (IsPaused)
            _engine.Resume();
        else
            _engine.Pause();
        await Task.Delay(50);
        PullSnapshot();
    }

    async Task StopRecordingAsync()
    {
        if (_finishRequested)
            return;
        _finishRequested = true;
        SetFinishing(true);
        Show("FinishingFile");
        SetBusy(true);
        try
        {
            await Task.Run(_engine.Stop);
            RecordingResult? result = _engine.LastResult;
            _finishRequested = false;
            SetFinishing(false);
            IsRecording = false;
            IsPaused = false;
            NotifyRecording();
            if (result?.Error != null)
                SetHint(result.Error);
            else if (result != null)
                ShowSaved(result.Path);
            RecordingStopped?.Invoke();
            PullSnapshot();
        }
        catch (Exception ex)
        {
            Log.Error("Не удалось остановить запись.", ex);
            SetHint(ex.Message);
        }
        finally
        {
            _finishRequested = false;
            SetFinishing(false);
            SetBusy(false);
        }
    }

    void TakeScreenshot()
    {
        if (_busy || _finishing)
            return;
        if (_monitor == null)
        {
            Show("NoMonitor");
            return;
        }

        SetBusy(true);
        try
        {
            BgraFrame frame = _engine.IsActive
                ? _engine.CopyCurrentFrame() ?? FrameGrabber.Capture(_monitor)
                : FrameGrabber.Capture(_monitor);
            string path = AppPaths.CreateScreenshotPath(AppPaths.ResolveOutputDirectory(_settings.OutputDirectory));
            BitmapSource bitmap = CreateBitmap(frame);
            SavePng(bitmap, path);
            Clipboard.SetImage(bitmap);
            Show("ScreenshotSaved", Path.GetFileName(path));
        }
        catch (Exception ex)
        {
            Log.Error("Скриншот не сохранился.", ex);
            SetHint(ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    void EditSettings()
    {
        ReloadMonitors();
        if (!SettingsWindow.TryEdit(Application.Current.MainWindow, _settings, _monitors, out AppSettings edited))
            return;

        _settings = edited;
        ConfigureMarker();
        _settings.Normalize();
        SettingsStore.Save(_settings);
        _monitor = MonitorEnumerator.Select(_monitors, _settings.MonitorDeviceName);
        _settings.MonitorDeviceName = _monitor?.DeviceName;
        SettingsStore.Save(_settings);
        RefreshLabels();
        SettingsChanged?.Invoke();
        _ = RefreshPreviewAsync();
    }

    void ConfigureMarker()
    {
        try { _markerController.SetEnabled(_settings.TemporaryMarker); }
        catch (Exception ex) { Log.Error("Marker initialization.", ex); SetHint(Loc.Format("MarkerUnavailable", ex.Message)); }
    }

    void CycleMonitor()
    {
        if (_monitors.Count < 2 || _monitor == null)
            return;
        int index = _monitors.ToList().FindIndex(item => item.DeviceName == _monitor.DeviceName);
        _monitor = _monitors[(index + 1) % _monitors.Count];
        _settings.MonitorDeviceName = _monitor.DeviceName;
        SettingsStore.Save(_settings);
        RefreshLabels();
        _ = RefreshPreviewAsync();
    }

    public void ReloadMonitors()
    {
        try
        {
            _monitors = MonitorEnumerator.GetMonitors();
            _monitor = MonitorEnumerator.Select(_monitors, _settings.MonitorDeviceName);
            _settings.MonitorDeviceName = _monitor?.DeviceName;
            RefreshLabels();
            _ = RefreshPreviewAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Не удалось перечислить мониторы.", ex);
            SetHint(ex.Message);
        }
    }

    async Task RefreshPreviewAsync()
    {
        if (_monitor == null || IsRecording)
            return;
        MonitorInfo monitor = _monitor;
        try
        {
            BgraFrame frame = await Task.Run(() => FrameGrabber.Capture(monitor, 960));
            var bitmap = CreateBitmap(frame);
            bitmap.Freeze();
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (!IsRecording && _monitor?.DeviceName == monitor.DeviceName)
                {
                    PreviewImage = bitmap;
                    OnPropertyChanged(nameof(PreviewImage));
                    OnPropertyChanged(nameof(ShowPlaceholder));
                }
            });
        }
        catch (Exception ex)
        {
            Log.Error("Превью монитора недоступно.", ex);
        }
    }

    void PullSnapshot()
    {
        RecordingSnapshot snapshot = _engine.Snapshot;
        bool finishing = _finishRequested || (_engine.IsActive && snapshot.State == RecordingState.Stopping);
        SetFinishing(finishing);
        if (finishing && _hintKey != "FinishingFile")
            Show("FinishingFile");

        bool recording = snapshot.State is RecordingState.Recording or RecordingState.Paused or RecordingState.Stopping or RecordingState.Starting;
        if (_engine.IsActive)
            recording = true;
        if (IsRecording && !_engine.IsActive && !_busy && !_finishRequested)
        {
            RecordingResult? result = _engine.LastResult;
            IsRecording = false;
            IsPaused = false;
            SetFinishing(false);
            if (!string.IsNullOrWhiteSpace(result?.Error))
                SetHint(result.Error);
            else if (result != null && result.WrittenFrames > 0)
                ShowSaved(result.Path);
            NotifyRecording();
            RaiseCommands();
            RecordingStopped?.Invoke();
        }
        else if (!_finishRequested && IsRecording != recording && snapshot.State == RecordingState.Idle && !_engine.IsActive)
        {
            IsRecording = false;
            IsPaused = false;
            SetFinishing(false);
            NotifyRecording();
        }
        else if (_engine.IsActive)
        {
            bool paused = snapshot.State == RecordingState.Paused;
            if (IsPaused != paused)
            {
                IsPaused = paused;
                OnPropertyChanged(nameof(IsPaused));
                OnPropertyChanged(nameof(ShowElapsed));
                OnPropertyChanged(nameof(PauseText));
            }
        }

        if (IsRecording || IsPaused || finishing || snapshot.State == RecordingState.Stopping)
        {
            TimerText = snapshot.Elapsed.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
            string size = FormatBytes(snapshot.FileBytes);
            string free = snapshot.FreeDiskBytes >= 0 ? FormatBytes(snapshot.FreeDiskBytes) : "—";
            StatsText = Loc.Format("Stats", snapshot.ActualFps, size, free);
            EncoderText = snapshot.EncoderName;
            if (!finishing && !string.IsNullOrWhiteSpace(snapshot.Message))
                SetHint(snapshot.Message!);
            OnPropertyChanged(nameof(TimerText));
            OnPropertyChanged(nameof(StatsText));
            OnPropertyChanged(nameof(EncoderText));
            OnPropertyChanged(nameof(ResolutionText));
        }

        SystemAudioLevel = SystemAudioActive ? snapshot.SystemAudioLevel : 0;
        MicrophoneLevel = MicrophoneActive ? snapshot.MicrophoneLevel : 0;
        NotifyAudioLevels();
        UpdateDiskEstimate(snapshot);
    }

    void OnPreview(BgraFrame frame)
    {
        if (_shuttingDown)
            return;
        var bitmap = CreateBitmap(frame);
        bitmap.Freeze();
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (_shuttingDown)
                return;
            PreviewImage = bitmap;
            OnPropertyChanged(nameof(PreviewImage));
            OnPropertyChanged(nameof(ShowPlaceholder));
        });
    }

    void OnLanguageChanged()
    {
        if (_hintKey != null)
            Hint = _hintArgs.Length == 0 ? Loc.Get(_hintKey) : Loc.Format(_hintKey, _hintArgs);
        RefreshLabels();
        OnPropertyChanged(nameof(RecordText));
        OnPropertyChanged(nameof(PauseText));
        RefreshTarget();
        RefreshDiskText();
        SyncRegionBorder();
        if (IsRecording || _finishing)
            PullSnapshot();
    }

    void RefreshLabels()
    {
        OnPropertyChanged(nameof(MonitorLabel));
        OnPropertyChanged(nameof(ResolutionText));
        OnPropertyChanged(nameof(QualityText));
        OnPropertyChanged(nameof(SystemAudio));
        OnPropertyChanged(nameof(Microphone));
        OnPropertyChanged(nameof(AudioNote));
        OnPropertyChanged(nameof(HasAudioNote));
        OnPropertyChanged(nameof(MinimizeOnRecord));
        RefreshMachineWarning();
        PublishDiskEstimate();
    }

    void SelectScreen()
    {
        _settings.Mode = CaptureMode.Monitor;
        SettingsStore.Save(_settings);
        RefreshTarget();
        SetHint("");
        SyncRegionBorder();
    }

    void SelectWindow()
    {
        _settings.Mode = CaptureMode.Window;
        SettingsStore.Save(_settings);
        RefreshTarget();
        SyncRegionBorder();
        SetBusy(true);
        try
        {
            if (!WindowPicker.TryPick(Application.Current.MainWindow, out WindowChoice choice))
            {
                Show(_window != 0 && WindowCatalog.IsAlive(_window) ? "TargetWindow" : "WindowHint", WindowLabel);
                return;
            }

            _window = choice.Handle;
            _windowTitle = choice.Title;
            _windowProcess = choice.ProcessName;
            RefreshTarget();
            Show("TargetWindow", WindowLabel);
        }
        finally
        {
            SetBusy(false);
        }
    }

    void SelectRegion()
    {
        _settings.Mode = CaptureMode.Region;
        SettingsStore.Save(_settings);
        RefreshTarget();
        _regionBorder.Hide();
        SetBusy(true);
        try
        {
            var current = new ScreenRect(_regionX, _regionY, _regionWidth, _regionHeight);
            if (!RegionWindow.TrySelect(Application.Current.MainWindow, current, out ScreenRect rect))
            {
                if (_regionWidth >= 16)
                    Show("TargetRegion", _regionWidth, _regionHeight);
                else
                    Show("RegionHint");
                return;
            }

            _regionX = rect.X;
            _regionY = rect.Y;
            _regionWidth = rect.Width;
            _regionHeight = rect.Height;
            _settings.RegionX = rect.X;
            _settings.RegionY = rect.Y;
            _settings.RegionWidth = rect.Width;
            _settings.RegionHeight = rect.Height;
            SettingsStore.Save(_settings);
            RefreshTarget();
            Show("TargetRegion", rect.Width, rect.Height);
        }
        finally
        {
            SyncRegionBorder();
            SetBusy(false);
        }
    }

    bool TryCreateRequest(out CaptureRequest request, out string errorKey)
    {
        request = null!;
        errorKey = "NoMonitor";
        if (_monitor == null)
            return false;

        IReadOnlyList<MonitorInfo> monitors = _monitors.Count > 0 ? _monitors : [_monitor];
        switch (_settings.Mode)
        {
            case CaptureMode.Window:
                if (!WindowCatalog.IsAlive(_window))
                {
                    bool hadTitle = _windowTitle.Length > 0;
                    _window = 0;
                    RefreshTarget();
                    errorKey = hadTitle ? "WindowGone" : "WindowHint";
                    return false;
                }

                request = new CaptureRequest
                {
                    Monitor = WindowCatalog.MonitorContaining(monitors, _window) ?? _monitor,
                    Window = _window
                };
                return true;
            case CaptureMode.Region:
                if (_regionWidth < 16 || _regionHeight < 16)
                {
                    errorKey = "RegionHint";
                    return false;
                }

                if (!WindowCatalog.TryIntersect(monitors, _regionX, _regionY, _regionWidth, _regionHeight, out MonitorInfo regionMonitor, out int cropX, out int cropY, out int cropWidth, out int cropHeight))
                {
                    errorKey = "RegionOffScreen";
                    return false;
                }

                request = new CaptureRequest
                {
                    Monitor = regionMonitor,
                    CropX = cropX,
                    CropY = cropY,
                    CropWidth = cropWidth,
                    CropHeight = cropHeight
                };
                return true;
            default:
                request = new CaptureRequest { Monitor = _monitor };
                return true;
        }
    }

    void SyncRegionBorder()
    {
        if (_settings.Mode == CaptureMode.Region && _regionWidth >= 16 && _regionHeight >= 16)
            _regionBorder.Show(_regionX, _regionY, _regionWidth, _regionHeight);
        else
            _regionBorder.Hide();
    }

    bool TryMoveRegion(ScreenRect rect)
    {
        if (_busy || _finishing || _shuttingDown || _settings.Mode != CaptureMode.Region)
            return false;
        if (_engine.IsActive && (_recordingMonitor == null ||
            !_engine.TryMoveRegion(rect.X - _recordingMonitor.Left, rect.Y - _recordingMonitor.Top)))
            return false;
        _regionX = _settings.RegionX = rect.X;
        _regionY = _settings.RegionY = rect.Y;
        _regionWidth = _settings.RegionWidth = rect.Width;
        _regionHeight = _settings.RegionHeight = rect.Height;
        OnPropertyChanged(nameof(TargetText));
        if (!IsRecording)
            Show("TargetRegion", rect.Width, rect.Height);
        return true;
    }

    void RefreshTarget()
    {
        OnPropertyChanged(nameof(ScreenTag));
        OnPropertyChanged(nameof(WindowTag));
        OnPropertyChanged(nameof(RegionTag));
        OnPropertyChanged(nameof(TargetText));
        RefreshMachineWarning();
        PublishDiskEstimate();
    }

    void SetFinishing(bool finishing)
    {
        if (_finishing == finishing)
            return;
        _finishing = finishing;
        _regionBorder.SetMoveEnabled(!_busy && !_finishing && !_shuttingDown);
        OnPropertyChanged(nameof(IsFinishing));
        OnPropertyChanged(nameof(ShowElapsed));
        RaiseCommands();
    }

    void ProbeHardware()
    {
        if (HardwareEncoderCatalog.CachedH264 is bool)
        {
            RefreshMachineWarning();
            return;
        }

        _ = Task.Run(() =>
        {
            bool ok = HardwareEncoderCatalog.TryProbe(out bool available);
            Dispatcher? dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.HasShutdownStarted)
                return;
            dispatcher.BeginInvoke(() =>
            {
                Log.Info(ok
                    ? $"Hardware H.264 {(available ? "found" : "not found")}."
                    : "Hardware H.264 check failed.");
                RefreshMachineWarning();
            });
        });
    }

    void RefreshMachineWarning()
    {
        try
        {
            (int width, int height) = EstimateOutput();
            long ram = RecordingSafety.AvailableMemory();
            string? key = RecordingSafety.RiskKey(ResolveSoftware(), width, height, _settings.Fps, ram);
            if (key != _riskKey)
            {
                _riskKey = key;
                Log.Info(key == null
                    ? $"Recording limits ok. RAM={ram / (1024 * 1024)}MB {width}x{height}@{_settings.Fps}"
                    : $"Recording limit {key}. RAM={ram / (1024 * 1024)}MB {width}x{height}@{_settings.Fps}");
            }

            string text = key == null ? "" : Loc.Get(key);
            if (text == _machineWarning)
                return;
            _machineWarning = text;
            OnPropertyChanged(nameof(MachineWarning));
            OnPropertyChanged(nameof(HasMachineWarning));
        }
        catch (Exception ex)
        {
            Log.Error("Не удалось оценить запас компьютера.", ex);
        }
    }

    bool? ResolveSoftware() => _settings.Encoder switch
    {
        EncoderPreference.Software => true,
        EncoderPreference.Hardware => false,
        _ => HardwareEncoderCatalog.CachedH264 is bool hardware ? !hardware : null
    };

    (int Width, int Height) EstimateOutput()
    {
        int width;
        int height;
        if (_settings.Mode == CaptureMode.Window && _window != 0 && WindowCatalog.TryGetBounds(_window, out _, out _, out int windowWidth, out int windowHeight))
        {
            width = windowWidth;
            height = windowHeight;
        }
        else if (_settings.Mode == CaptureMode.Region && _regionWidth >= 16 && _regionHeight >= 16)
        {
            width = _regionWidth;
            height = _regionHeight;
        }
        else if (_monitor != null)
        {
            width = _monitor.Width;
            height = _monitor.Height;
        }
        else
        {
            return (0, 0);
        }

        return OutputSize.Compute(width, height, _settings.Resolution);
    }

    int EstimatedBitrate()
    {
        if ((IsRecording || _finishing) && _engine.Snapshot.Bitrate > 0)
            return _engine.Snapshot.Bitrate;
        (int width, int height) = EstimateOutput();
        return RecordingSafety.BitsPerSecond(_settings, width, height);
    }

    string EstimateDirectory()
    {
        if ((IsRecording || _finishing) && !string.IsNullOrWhiteSpace(_engine.Snapshot.OutputPath))
            return _engine.Snapshot.OutputPath!;
        return string.IsNullOrWhiteSpace(_settings.OutputDirectory)
            ? AppPaths.DefaultOutputDirectory
            : _settings.OutputDirectory;
    }

    void UpdateDiskEstimate(RecordingSnapshot snapshot)
    {
        bool live = IsRecording || IsPaused || _finishing || snapshot.State == RecordingState.Stopping;
        if (live && snapshot.FreeDiskBytes >= 0)
        {
            int bitrate = snapshot.Bitrate > 0 ? snapshot.Bitrate : EstimatedBitrate();
            int minutes = RecordingSafety.MinutesThatFit(snapshot.FreeDiskBytes, bitrate);
            bool low = RecordingSafety.DiskIsLow(minutes);
            bool due = DateTime.UtcNow >= _nextDiskUtc;
            if (!due && low == _diskEstimateLow && _shownFree >= 0)
                return;
            if (due)
                _nextDiskUtc = DateTime.UtcNow.AddSeconds(3);
            ApplyDisk(snapshot.FreeDiskBytes, RecordingSafety.DriveLabel(snapshot.OutputPath ?? EstimateDirectory()), bitrate);
            return;
        }

        if (DateTime.UtcNow < _nextDiskUtc)
            return;
        _nextDiskUtc = DateTime.UtcNow.AddSeconds(3);
        QueueDiskSample();
    }

    void PublishDiskEstimate()
    {
        RecordingSnapshot snapshot = _engine.Snapshot;
        if ((IsRecording || _finishing || snapshot.State == RecordingState.Stopping) && snapshot.FreeDiskBytes >= 0)
        {
            int bitrate = snapshot.Bitrate > 0 ? snapshot.Bitrate : EstimatedBitrate();
            ApplyDisk(snapshot.FreeDiskBytes, RecordingSafety.DriveLabel(snapshot.OutputPath ?? EstimateDirectory()), bitrate);
            return;
        }

        QueueDiskSample();
    }

    void QueueDiskSample()
    {
        if (Interlocked.CompareExchange(ref _diskBusy, 1, 0) != 0)
            return;
        string path = EstimateDirectory();
        int bitrate = EstimatedBitrate();
        _ = Task.Run(() =>
        {
            long free = RecordingSafety.AvailableDisk(path);
            string drive = RecordingSafety.DriveLabel(path);
            Dispatcher? dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.HasShutdownStarted)
            {
                Interlocked.Exchange(ref _diskBusy, 0);
                return;
            }

            dispatcher.BeginInvoke(() =>
            {
                try
                {
                    if (!IsRecording && !_finishing && _engine.Snapshot.State != RecordingState.Stopping)
                        ApplyDisk(free, drive, bitrate);
                }
                finally
                {
                    Interlocked.Exchange(ref _diskBusy, 0);
                }
            });
        });
    }

    void RefreshDiskText()
    {
        if (_shownFree < 0 && _diskEstimate.Length == 0)
            return;
        ApplyDisk(_shownFree, _shownDrive, _shownBitrate);
    }

    void ApplyDisk(long freeBytes, string drive, int bitrate)
    {
        _shownFree = freeBytes;
        _shownDrive = drive;
        _shownBitrate = bitrate;
        if (freeBytes < 0 || bitrate <= 0 || drive.Length == 0)
        {
            SetDisk("", false);
            return;
        }

        int minutes = RecordingSafety.MinutesThatFit(freeBytes, bitrate);
        bool low = RecordingSafety.DiskIsLow(minutes);
        SetDisk(Loc.Format(low ? "DiskEstimateLow" : "DiskEstimate", minutes, drive), low);
    }

    void SetDisk(string text, bool low)
    {
        if (_diskEstimate == text && _diskEstimateLow == low)
            return;
        _diskEstimate = text;
        _diskEstimateLow = low;
        OnPropertyChanged(nameof(DiskEstimate));
        OnPropertyChanged(nameof(DiskEstimateLow));
        OnPropertyChanged(nameof(HasDiskEstimate));
    }

    void ShowSaved(string path)
    {
        string full = Path.GetFullPath(path);
        _savedFile = File.Exists(full) ? full : null;
        Show("Saved", Path.GetFileName(full));
        OnPropertyChanged(nameof(CanOpenFolder));
        OpenFolderCommand.RaiseCanExecuteChanged();
    }

    void ClearSavedFile()
    {
        if (_savedFile == null)
            return;
        _savedFile = null;
        OnPropertyChanged(nameof(CanOpenFolder));
        OpenFolderCommand.RaiseCanExecuteChanged();
    }

    void OpenSavedFolder()
    {
        string? path = _savedFile;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            ClearSavedFile();
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = "/select,\"" + path + "\"",
            UseShellExecute = true
        });
    }

    void NotifyRecording()
    {
        OnPropertyChanged(nameof(IsRecording));
        OnPropertyChanged(nameof(IsPaused));
        OnPropertyChanged(nameof(ShowElapsed));
        OnPropertyChanged(nameof(RecordText));
        OnPropertyChanged(nameof(PauseText));
        OnPropertyChanged(nameof(ResolutionText));
        NotifyAudioLevels();
    }

    void NotifyAudioLevels()
    {
        OnPropertyChanged(nameof(SystemAudioLevel));
        OnPropertyChanged(nameof(MicrophoneLevel));
        OnPropertyChanged(nameof(SystemAudioActive));
        OnPropertyChanged(nameof(MicrophoneActive));
    }

    void SetBusy(bool busy)
    {
        _busy = busy || _shuttingDown;
        _regionBorder.SetMoveEnabled(!_busy && !_finishing);
        RaiseCommands();
    }

    void RaiseCommands()
    {
        _regionBorder.SetEditEnabled(RegionModeCommand.CanExecute(null) && !_finishing && !_shuttingDown);
        RecordCommand.RaiseCanExecuteChanged();
        PauseCommand.RaiseCanExecuteChanged();
        ScreenshotCommand.RaiseCanExecuteChanged();
        SettingsCommand.RaiseCanExecuteChanged();
        CycleMonitorCommand.RaiseCanExecuteChanged();
        ScreenModeCommand.RaiseCanExecuteChanged();
        WindowModeCommand.RaiseCanExecuteChanged();
        RegionModeCommand.RaiseCanExecuteChanged();
    }

    static BitmapSource CreateBitmap(BgraFrame frame) =>
        BitmapSource.Create(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null, frame.Pixels, frame.Stride);

    static void SavePng(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    static string FormatBytes(long bytes)
    {
        if (bytes < 0)
            return "—";
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.#} {units[unit]}";
    }
}

public abstract class ObservableObject : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
}

public sealed class RelayCommand : System.Windows.Input.ICommand
{
    readonly Action _execute;
    readonly Func<bool>? _can;

    public RelayCommand(Action execute, Func<bool>? can = null)
    {
        _execute = execute;
        _can = can;
    }

    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => _can?.Invoke() ?? true;
    public void Execute(object? parameter)
    {
        if (CanExecute(parameter))
            _execute();
    }
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
