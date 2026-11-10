using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using FrameForge.Core;
using FrameForge.Diagnostics;

namespace FrameForge.UI;

public partial class MainWindow : Window
{
    readonly MainViewModel _model;
    HwndSource? _source;
    bool _closing;
    bool _canClose;

    public MainWindow()
    {
        InitializeComponent();
        _model = new MainViewModel();
        _model.RecordingStarted += OnRecordingStarted;
        _model.SettingsChanged += RegisterHotkeys;
        DataContext = _model;
        Loaded += (_, _) => MarkTitleButtonsInteractive(TitleBar);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _source = (HwndSource)PresentationSource.FromVisual(this)!;
        _source.AddHook(WndProc);
        SetWindowDisplayAffinity(_source.Handle, 0x00000011);
        RegisterHotkeys();
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        if (_canClose)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        base.OnClosing(e);
        if (_closing)
            return;

        _closing = true;
        IsEnabled = false;
        try
        {
            await _model.ShutdownAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Не удалось завершить работу программы.", ex);
        }
        finally
        {
            _canClose = true;
            Close();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_source != null)
        {
            UnregisterHotKey(_source.Handle, 1);
            UnregisterHotKey(_source.Handle, 2);
            UnregisterHotKey(_source.Handle, 3);
            _source.RemoveHook(WndProc);
        }

        base.OnClosed(e);
    }

    void RegisterHotkeys()
    {
        if (_source == null)
            return;

        UnregisterHotKey(_source.Handle, 1);
        UnregisterHotKey(_source.Handle, 2);
        UnregisterHotKey(_source.Handle, 3);
        string?[] failures =
        [
            Register(_model.Settings.StartStopHotkey, 1),
            Register(_model.Settings.PauseHotkey, 2),
            Register(_model.Settings.ScreenshotHotkey, 3)
        ];
        string failed = string.Join(", ", failures.Where(text => text != null));
        if (failed.Length > 0)
            _model.Show("HotkeyBusy", failed);
    }

    string? Register(string text, int id)
    {
        if (!Hotkey.TryParse(text, out Hotkey hotkey))
            return text;
        return RegisterHotKey(_source!.Handle, id, hotkey.Modifiers, hotkey.VirtualKey) ? null : text;
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x0312)
        {
            switch (wParam.ToInt32())
            {
                case 1: _model.RequestRecord(); break;
                case 2: _model.RequestPause(); break;
                case 3: _model.RequestScreenshot(); break;
            }
            handled = true;
        }
        else if (msg == 0x00A3 && wParam.ToInt32() == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            handled = true;
        }

        return IntPtr.Zero;
    }

    void OnRecordingStarted()
    {
        if (_model.MinimizeOnRecord)
            WindowState = WindowState.Minimized;
    }

    void Title_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (IsInsideButton(e.OriginalSource as DependencyObject))
            return;

        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            return;
        }

        if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed)
            return;

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
            DragMove();
        else
            SendMessage(hwnd, 0xA1, (IntPtr)2, IntPtr.Zero);
    }

    static void MarkTitleButtonsInteractive(DependencyObject root)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Button button)
            {
                button.ApplyTemplate();
                MarkChromeInteractive(button);
            }
            else
            {
                MarkTitleButtonsInteractive(child);
            }
        }
    }

    static void MarkChromeInteractive(DependencyObject node)
    {
        if (node is IInputElement input)
            WindowChrome.SetIsHitTestVisibleInChrome(input, true);
        int count = VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < count; i++)
            MarkChromeInteractive(VisualTreeHelper.GetChild(node, i));
    }

    static bool IsInsideButton(DependencyObject? source)
    {
        while (source != null)
        {
            if (source is Button)
                return true;
            source = VisualTreeHelper.GetParent(source) ?? LogicalTreeHelper.GetParent(source);
        }

        return false;
    }

    void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    void Close_Click(object sender, RoutedEventArgs e) => Close();

    [DllImport("user32.dll")]
    static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);

    [DllImport("user32.dll")]
    static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    [DllImport("user32.dll")]
    static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
}

public sealed class InverseBoolConverter : System.Windows.Data.IValueConverter
{
    public static InverseBoolConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
        value is true ? false : true;

    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
        value is true ? false : true;
}
