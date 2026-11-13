using System.Diagnostics;
using FrameForge.Core;

namespace FrameForge.UI;

public sealed class AppUpdateViewModel : ObservableObject, IDisposable
{
    readonly UpdateChecker _checker;
    readonly Action<Uri> _openDownload;
    readonly CancellationTokenSource _lifetime = new();
    readonly Version _installed;
    Task _checkTask = Task.CompletedTask;
    AppUpdate? _available;
    bool _checking, _checked, _failed, _downloadFailed, _showNotice, _manual, _started, _disposed;

    public AppUpdateViewModel(UpdateChecker? checker = null, Action<Uri>? openDownload = null, Version? installed = null)
    {
        _checker = checker ?? new UpdateChecker();
        _installed = installed ?? typeof(AppUpdateViewModel).Assembly.GetName().Version ?? new Version(1, 0, 0);
        _openDownload = openDownload ?? (uri => Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }));
        CheckCommand = new RelayCommand(() => _ = CheckAsync(), () => !_checking && !_disposed);
        DownloadCommand = new RelayCommand(Download, () => HasUpdate && !_disposed);
        DismissCommand = new RelayCommand(() => { _showNotice = false; Notify(); });
        Loc.Changed += Notify;
    }

    public RelayCommand CheckCommand { get; }
    public RelayCommand DownloadCommand { get; }
    public RelayCommand DismissCommand { get; }
    public bool IsChecking => _checking;
    public bool HasUpdate => _available != null;
    public bool ShowNotice => _showNotice && Status.Length > 0;
    public string Status => _checking ? Loc.Get("UpdateChecking") : _downloadFailed ? Loc.Get("UpdateDownloadFailed")
        : _available != null ? Loc.Format("UpdateAvailable", _available.Version.ToString(3))
        : _failed ? Loc.Get("UpdateCheckFailed") : _checked ? Loc.Format("UpdateCurrent", _installed.ToString(3)) : "";

    public Task StartAsync(bool enabled)
    {
        if (!enabled || _started || _disposed) return Task.CompletedTask;
        _started = true;
        return CheckAsync(manual: false);
    }

    public Task CheckAsync(bool manual = true)
    {
        if (_disposed) return Task.CompletedTask;
        if (manual) { _manual = true; _showNotice = true; Notify(); }
        if (_checking) return _checkTask;
        _manual = manual;
        _checkTask = CheckCoreAsync();
        return _checkTask;
    }

    async Task CheckCoreAsync()
    {
        _checking = true;
        _failed = _downloadFailed = false;
        Notify();
        try
        {
            AppUpdate? update = await _checker.CheckAsync(_installed, _lifetime.Token);
            if (_disposed) return;
            _available = update;
            _checked = true;
            _showNotice = update != null || _manual;
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch
        {
            if (_disposed) return;
            _failed = true;
            _showNotice = _manual || _available != null;
        }
        finally { _checking = false; if (!_disposed) Notify(); }
    }

    void Download()
    {
        if (_available == null) return;
        try { _openDownload(_available.DownloadUri); _downloadFailed = false; }
        catch { _downloadFailed = true; }
        Notify();
    }

    void Notify()
    {
        OnPropertyChanged(nameof(IsChecking));
        OnPropertyChanged(nameof(HasUpdate));
        OnPropertyChanged(nameof(ShowNotice));
        OnPropertyChanged(nameof(Status));
        CheckCommand.RaiseCanExecuteChanged();
        DownloadCommand.RaiseCanExecuteChanged();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Loc.Changed -= Notify;
        _lifetime.Cancel();
        _lifetime.Dispose();
        CheckCommand.RaiseCanExecuteChanged();
        DownloadCommand.RaiseCanExecuteChanged();
    }
}
