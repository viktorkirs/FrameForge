using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using FrameForge.Capture;
using FrameForge.Core;
using FrameForge.Diagnostics;

namespace FrameForge.UI;

sealed class MarkerController : IDisposable
{
    readonly MarkerState _state;
    readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    readonly HookProc _callback;
    readonly List<MarkerWindow> _windows = [];
    nint _hook;
    bool _enabled, _leftAlt, _rightAlt, _suspended, _requestedActive;
    int _generation;

    public MarkerController(MarkerState state)
    {
        _state = state;
        _callback = Keyboard;
        _state.Changed += Redraw;
    }

    public void SetEnabled(bool enabled)
    {
        if (_enabled == enabled) return;
        if (enabled)
        {
            _hook = SetWindowsHookEx(13, _callback, GetModuleHandle(null), 0);
            if (_hook == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            _enabled = true;
            _generation++;
        }
        else
        {
            _enabled = false;
            _generation++;
            if (_hook != 0) UnhookWindowsHookEx(_hook);
            _hook = 0;
            _leftAlt = _rightAlt = _suspended = _requestedActive = false;
            _state.SetActive(false);
            CloseWindows();
        }
    }

    nint Keyboard(int code, nint message, nint data)
    {
        if (code >= 0 && _enabled)
        {
            var key = Marshal.PtrToStructure<KeyboardData>(data);
            bool down = message == 0x100 || message == 0x104;
            bool up = message == 0x101 || message == 0x105;
            bool alt = key.Key is 0x12 or 0xA4 or 0xA5;
            if (alt && (down || up))
            {
                bool right = key.Key == 0xA5 || (key.Key == 0x12 && (key.Flags & 1) != 0);
                if (right) _rightAlt = down; else _leftAlt = down;
                if (!_leftAlt && !_rightAlt) _suspended = false;
                else if ((GetAsyncKeyState(0x11) & 0x8000) != 0) _suspended = true; // AltGr remains available.
                QueueState();
            }
            else if (down && (_leftAlt || _rightAlt))
            {
                _suspended = true; // Leave Alt+Tab, Alt+F4 and menu shortcuts to Windows.
                QueueState();
            }
        }
        return CallNextHookEx(_hook, code, message, data);
    }

    void QueueState()
    {
        if (_dispatcher.HasShutdownStarted) return;
        bool active = _enabled && (_leftAlt || _rightAlt) && !_suspended;
        if (_requestedActive == active) return;
        _requestedActive = active;
        // Do not coalesce an Alt release with the next press: every release must clear ink.
        if (!active) _state.SetActive(false);
        int generation = _generation;
        _dispatcher.BeginInvoke(() =>
        {
            if (!_enabled || generation != _generation) return;
            try
            {
                _state.SetActive(active);
                if (active)
                {
                    if (_windows.Count == 0)
                        foreach (MonitorInfo monitor in MonitorEnumerator.GetMonitors())
                            _windows.Add(new MarkerWindow(_state, monitor));
                    foreach (MarkerWindow window in _windows) window.ShowOnMonitor();
                }
                // Hidden layered windows retain their last rendered bitmap. Destroy the
                // surfaces so the next press cannot briefly present that old bitmap.
                else CloseWindows();
            }
            catch (Exception ex)
            {
                Log.Error("Screen marker unavailable.", ex);
                SetEnabled(false);
            }
        }, DispatcherPriority.Input);
    }

    void CloseWindows()
    {
        MarkerWindow[] closing = _windows.ToArray();
        _windows.Clear();
        foreach (MarkerWindow window in closing) window.Close();
    }

    void Redraw()
    {
        if (!_dispatcher.CheckAccess()) { _dispatcher.BeginInvoke(Redraw); return; }
        foreach (MarkerWindow window in _windows) window.InvalidateVisual();
    }

    public void Dispose() { SetEnabled(false); _state.Changed -= Redraw; }

    delegate nint HookProc(int code, nint message, nint data);
    [StructLayout(LayoutKind.Sequential)] struct KeyboardData { public uint Key, Scan, Flags, Time; public nint Extra; }
    [DllImport("user32.dll", SetLastError = true)] static extern nint SetWindowsHookEx(int id, HookProc callback, nint module, uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern nint GetModuleHandle(string? name);
}

sealed class MarkerWindow : Window
{
    readonly MarkerState _state;
    readonly MonitorInfo _monitor;
    bool _drawing;

    public MarkerWindow(MarkerState state, MonitorInfo monitor)
    {
        _state = state;
        _monitor = monitor;
        Title = "FrameForge Marker";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
        Cursor = Cursors.Pen;
        Width = monitor.Width;
        Height = monitor.Height;
        Left = monitor.Left;
        Top = monitor.Top;
        SourceInitialized += (_, _) =>
        {
            nint hwnd = new WindowInteropHelper(this).Handle;
            SetWindowLongPtr(hwnd, -20, GetWindowLongPtr(hwnd, -20) | 0x08000080);
            SetWindowDisplayAffinity(hwnd, 0x11);
        };
        MouseLeftButtonDown += (_, e) =>
        {
            if (!GetCursorPos(out NativePoint point)) return;
            _drawing = CaptureMouse();
            _state.BeginStroke(point.X, point.Y, (float)(12 * VisualTreeHelper.GetDpi(this).DpiScaleX));
            e.Handled = true;
        };
        MouseMove += (_, e) =>
        {
            if (_drawing && e.LeftButton == MouseButtonState.Pressed && GetCursorPos(out NativePoint point))
                _state.AddPoint(point.X, point.Y);
            e.Handled = true;
        };
        MouseLeftButtonUp += (_, e) => { EndStroke(); e.Handled = true; };
        LostMouseCapture += (_, _) => EndStroke();
    }

    void EndStroke()
    {
        _drawing = false;
        _state.EndStroke();
        if (IsMouseCaptured) ReleaseMouseCapture();
    }

    public void ShowOnMonitor()
    {
        if (!IsVisible) Show();
        SetWindowPos(new WindowInteropHelper(this).Handle, new nint(-1), _monitor.Left, _monitor.Top, _monitor.Width, _monitor.Height, 0x10);
    }

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        var group = new DrawingGroup { Opacity = .5 };
        using (DrawingContext ink = group.Open())
            foreach (MarkerSegment segment in _state.Snapshot().Segments)
            {
                var pen = new Pen(new SolidColorBrush(Color.FromRgb(255, 216, 51)), segment.Width / dpi.DpiScaleX) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                ink.DrawLine(pen, new Point((segment.Start.X - _monitor.Left) / dpi.DpiScaleX, (segment.Start.Y - _monitor.Top) / dpi.DpiScaleY),
                    new Point((segment.End.X - _monitor.Left) / dpi.DpiScaleX, (segment.End.Y - _monitor.Top) / dpi.DpiScaleY));
            }
        drawing.DrawDrawing(group);
    }

    [StructLayout(LayoutKind.Sequential)] struct NativePoint { public int X, Y; }
    [DllImport("user32.dll")] static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(nint hwnd, uint affinity);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
}
