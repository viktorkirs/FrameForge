using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using FrameForge.Core;

namespace FrameForge.UI;

// The content inside the frame remains click-through. This small grip moves the frame.
sealed class RegionMoveHandle : Window
{
    readonly TextBlock _label;
    readonly Button _edit;
    readonly Border _grip;
    readonly Func<ScreenRect, bool> _move;
    readonly Action _complete;
    ScreenRect _rect;
    ScreenRect _monitor;
    ScreenRect _origin;
    NativePoint _anchor;
    bool _dragging;
    bool _editEnabled = true;

    public RegionMoveHandle(Func<ScreenRect, bool> move, Action complete, Action edit)
    {
        _move = move;
        _complete = complete;
        Title = Loc.Get("MoveRegion");
        Width = 470;
        Height = 38;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Background = (Brush)Application.Current.FindResource("CardBrush");
        Foreground = (Brush)Application.Current.FindResource("TextBrush");
        Cursor = Cursors.Arrow;
        _label = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center, FontSize = 12 };
        _grip = new Border { Background = Brushes.Transparent, Cursor = Cursors.SizeAll, Child = _label, ToolTip = Loc.Get("RegionMoveHint") };
        _edit = new Button { Content = "✎ " + Loc.Get("EditRegion"), Padding = new Thickness(10, 0, 10, 0), Margin = new Thickness(8, 4, 0, 4), FontSize = 12 };
        _edit.Background = (Brush)Application.Current.FindResource("AccentBrush");
        _edit.Foreground = Brushes.White;
        AutomationProperties.SetAutomationId(_edit, "EditConfirmedRegion");
        _edit.Click += (_, _) => edit();
        var panel = new Grid();
        panel.ColumnDefinitions.Add(new ColumnDefinition());
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        panel.Children.Add(_grip);
        Grid.SetColumn(_edit, 1);
        panel.Children.Add(_edit);
        Content = new Border
        {
            BorderBrush = (Brush)Application.Current.FindResource("AccentBrush"),
            BorderThickness = new Thickness(1), Padding = new Thickness(8, 0, 4, 0), Child = panel
        };
        SourceInitialized += (_, _) => SetWindowDisplayAffinity(new WindowInteropHelper(this).Handle, 0x11);
        _grip.MouseLeftButtonDown += (_, e) =>
        {
            if (!IsEnabled || !GetCursorPos(out _anchor))
                return;
            _origin = _rect;
            _dragging = _grip.CaptureMouse();
            e.Handled = true;
        };
        _grip.MouseMove += (_, e) =>
        {
            if (!_dragging || e.LeftButton != MouseButtonState.Pressed || !GetCursorPos(out NativePoint point))
                return;
            ScreenRect next = RegionPlacement.MoveWithin(_origin, point.X - _anchor.X, point.Y - _anchor.Y, _monitor);
            if (!next.IsEmpty && next != _rect)
                _move(next);
            e.Handled = true;
        };
        _grip.MouseLeftButtonUp += (_, e) => { FinishDrag(); e.Handled = true; };
        _grip.LostMouseCapture += (_, _) => FinishDrag();
    }

    public void SetEditEnabled(bool enabled)
    {
        _editEnabled = enabled;
        _edit.IsEnabled = enabled;
        _edit.ToolTip = Loc.Get(enabled ? "EditRegionHint" : "EditRegionWhileRecording");
    }

    void FinishDrag()
    {
        if (!_dragging)
            return;
        _dragging = false;
        _grip.ReleaseMouseCapture();
        _complete();
    }

    public void ShowAt(ScreenRect region, ScreenRect monitor, bool enabled)
    {
        _rect = region;
        _monitor = monitor;
        IsEnabled = enabled;
        Title = Loc.Get("MoveRegion");
        _grip.ToolTip = Loc.Get("RegionMoveHint");
        _edit.Content = "✎ " + Loc.Get("EditRegion");
        SetEditEnabled(_editEnabled);
        _label.Text = $"↔  {Loc.Get("MoveRegion")} · {region.Width} × {region.Height}";
        if (!IsVisible)
            Show();
        nint hwnd = new WindowInteropHelper(this).Handle;
        double scale = GetDpiForWindow(hwnd) / 96d;
        if (scale <= 0)
            scale = 1;
        int width = Math.Min(monitor.Width, (int)Math.Ceiling(Width * scale));
        int height = Math.Min(monitor.Height, (int)Math.Ceiling(Height * scale));
        int x = Math.Clamp(region.X, monitor.X, monitor.X + monitor.Width - width);
        int y = region.Y - height - 6;
        if (y < monitor.Y)
            y = region.Y + 6;
        y = Math.Clamp(y, monitor.Y, monitor.Y + monitor.Height - height);
        SetWindowPos(hwnd, new nint(-1), x, y, width, height, 0x0010);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct NativePoint { public int X; public int Y; }
    [DllImport("user32.dll")] static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(nint hwnd, uint affinity);
    [DllImport("user32.dll")] static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
}
