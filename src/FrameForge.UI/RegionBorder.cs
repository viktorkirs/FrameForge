using System.Runtime.InteropServices;
using FrameForge.Core;
using FrameForge.Diagnostics;

namespace FrameForge.UI;

/// <summary>
/// Thin click-through frame around a confirmed capture rectangle.
/// Position and size are physical pixels on the monitor that contains the rectangle.
/// WDA_EXCLUDEFROMCAPTURE keeps the frame out of the recording.
/// </summary>
sealed class RegionBorder : IDisposable
{
    const int WsExTransparent = 0x00000020;
    const int WsExToolWindow = 0x00000080;
    const int WsExTopmost = 0x00000008;
    const int WsExNoActivate = 0x08000000;
    const int WsPopup = unchecked((int)0x80000000);
    const uint SwpNoActivate = 0x0010;
    const uint SwpNoRedraw = 0x0008;
    const int SwHide = 0;
    const int SwShowNoActivate = 4;
    const uint WdaExcludeFromCapture = 0x00000011;
    const int WmEraseBkgnd = 0x0014;
    const int WmPaint = 0x000F;
    const int WmDpiChanged = 0x02E0;
    const int White = 0x00FFFFFF;
    const int Accent = 0x00E06F2F;
    const int GwlUserData = -21;

    static readonly WndProc Proc = OnMessage;
    static readonly IntPtr ProcPtr = Marshal.GetFunctionPointerForDelegate(Proc);
    static bool _classReady;
    static IntPtr _module;
    static IntPtr _whiteBrush;
    static IntPtr _accentBrush;
    static readonly Dictionary<IntPtr, FrameBox> Shown = new();
    static bool _affinityWarned;
    static bool _placing;

    IntPtr _halo;
    IntPtr _edge;
    bool _disposed;
    RegionMoveHandle? _handle;
    bool _moveEnabled = true;
    bool _editEnabled = true;
    ScreenRect _region;

    public Func<ScreenRect, bool>? MoveRequested { get; set; }
    public event Action? MoveCompleted;
    public event Action? EditRequested;

    public void SetEditEnabled(bool enabled)
    {
        _editEnabled = enabled;
        _handle?.SetEditEnabled(enabled);
    }

    public void SetMoveEnabled(bool enabled)
    {
        _moveEnabled = enabled;
        if (_handle != null)
            _handle.IsEnabled = enabled;
    }

    public void Show(int x, int y, int width, int height)
    {
        if (_disposed || width < 16 || height < 16 || width > 16384 || height > 16384)
        {
            Hide();
            return;
        }

        try
        {
            MonitorBox monitor = MonitorFor(x, y, width, height);
            int thickness = ThicknessFor(monitor.Dpi);
            if (!EnsureWindows())
                return;

            FrameBox halo = Layout(x, y, width, height, thickness, monitor);
            FrameBox edge = Layout(x - 1, y - 1, width + 2, height + 2, Math.Max(1, thickness - 2), monitor);
            Place(_halo, halo);
            Place(_edge, edge);
            _region = new ScreenRect(x, y, width, height);
            _handle ??= new RegionMoveHandle(next =>
            {
                if (!_moveEnabled || MoveRequested?.Invoke(next) != true)
                    return false;
                Show(next.X, next.Y, next.Width, next.Height);
                return true;
            }, () => MoveCompleted?.Invoke(), () =>
            {
                if (!_disposed && _moveEnabled && _editEnabled)
                    EditRequested?.Invoke();
            });
            _handle.SetEditEnabled(_editEnabled);
            _handle.ShowAt(_region, new ScreenRect(monitor.Left, monitor.Top, monitor.Right - monitor.Left, monitor.Bottom - monitor.Top), _moveEnabled);
        }
        catch (Exception ex)
        {
            Log.Error("Не удалось показать рамку области.", ex);
            Hide();
        }
    }

    public void Hide()
    {
        _handle?.Hide();
        if (_halo != IntPtr.Zero)
            ShowWindow(_halo, SwHide);
        if (_edge != IntPtr.Zero)
            ShowWindow(_edge, SwHide);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _handle?.Close();
        _handle = null;
        Destroy(ref _halo);
        Destroy(ref _edge);
    }

    bool EnsureWindows()
    {
            EnsureClass();
            _halo = Ensure(_halo, White);
        _edge = Ensure(_edge, Accent);
        return _halo != IntPtr.Zero && _edge != IntPtr.Zero;
    }

    static IntPtr Ensure(IntPtr hwnd, int color)
    {
        if (hwnd != IntPtr.Zero)
            return hwnd;

        hwnd = CreateWindowEx(
            WsExTransparent | WsExToolWindow | WsExTopmost | WsExNoActivate,
            "FrameForgeRegionBorder",
            "",
            WsPopup,
            0,
            0,
            0,
            0,
            IntPtr.Zero,
            IntPtr.Zero,
            _module,
            IntPtr.Zero);
        if (hwnd == IntPtr.Zero)
        {
            Log.Error($"Не удалось создать рамку области ({Marshal.GetLastWin32Error()}).");
            return IntPtr.Zero;
        }

        SetUserData(hwnd, color);
        Exclude(hwnd);
        return hwnd;
    }

    static void Place(IntPtr hwnd, FrameBox frame)
    {
        if (hwnd == IntPtr.Zero)
            return;
        if (frame.Width < 2 || frame.Height < 2 || frame.HoleW < 1 || frame.HoleH < 1)
        {
            ShowWindow(hwnd, SwHide);
            return;
        }

        Shown[hwnd] = frame;
        _placing = true;
        try
        {
            KeepPhysicalSize(hwnd, frame);
            IntPtr region = CreateFrameRegion(frame.Width, frame.Height, frame.HoleX, frame.HoleY, frame.HoleW, frame.HoleH);
            if (region == IntPtr.Zero || SetWindowRgn(hwnd, region, false) == 0)
            {
                if (region != IntPtr.Zero)
                    DeleteObject(region);
                ShowWindow(hwnd, SwHide);
                return;
            }

            ShowWindow(hwnd, SwShowNoActivate);
            KeepPhysicalSize(hwnd, frame);
            Exclude(hwnd);
            InvalidateRect(hwnd, IntPtr.Zero, true);
            UpdateWindow(hwnd);
        }
        finally
        {
            _placing = false;
        }
    }

    static void KeepPhysicalSize(IntPtr hwnd, FrameBox frame) =>
        SetWindowPos(hwnd, new IntPtr(-1), frame.X, frame.Y, frame.Width, frame.Height, SwpNoActivate | SwpNoRedraw);

    static void Exclude(IntPtr hwnd)
    {
        if (SetWindowDisplayAffinity(hwnd, WdaExcludeFromCapture) || _affinityWarned)
            return;
        _affinityWarned = true;
        Log.Error("Не удалось исключить рамку области из захвата.");
    }

    static FrameBox Layout(int x, int y, int width, int height, int thickness, MonitorBox monitor)
    {
        int left = Math.Max(x - thickness, monitor.Left);
        int top = Math.Max(y - thickness, monitor.Top);
        int right = Math.Min(x + width + thickness, monitor.Right);
        int bottom = Math.Min(y + height + thickness, monitor.Bottom);
        if (right <= left || bottom <= top)
        {
            left = x;
            top = y;
            right = x + width;
            bottom = y + height;
        }

        int holeL = x;
        int holeT = y;
        int holeR = x + width;
        int holeB = y + height;
        if (x - left < thickness)
            holeL += thickness - (x - left);
        if (y - top < thickness)
            holeT += thickness - (y - top);
        if (right - (x + width) < thickness)
            holeR -= thickness - (right - (x + width));
        if (bottom - (y + height) < thickness)
            holeB -= thickness - (bottom - (y + height));

        holeL = Math.Clamp(holeL, left, Math.Max(left, right - 1));
        holeT = Math.Clamp(holeT, top, Math.Max(top, bottom - 1));
        holeR = Math.Clamp(holeR, holeL + 1, Math.Max(holeL + 1, right));
        holeB = Math.Clamp(holeB, holeT + 1, Math.Max(holeT + 1, bottom));
        return new FrameBox(left, top, right - left, bottom - top, holeL - left, holeT - top, holeR - holeL, holeB - holeT);
    }

    static int ThicknessFor(int dpi)
    {
        if (dpi < 96)
            dpi = 96;
        int px = (int)Math.Round(3.0 * dpi / 96.0, MidpointRounding.AwayFromZero);
        if (px < 3)
            return 3;
        if (px > 8)
            return 8;
        return px;
    }

    static MonitorBox MonitorFor(int x, int y, int width, int height)
    {
        var rect = new RECT { Left = x, Top = y, Right = x + width, Bottom = y + height };
        IntPtr monitor = MonitorFromRect(ref rect, 2);
        if (monitor != IntPtr.Zero)
        {
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(monitor, ref info))
            {
                int dpi = 96;
                try
                {
                    if (GetDpiForMonitor(monitor, 0, out uint dpiX, out _) == 0 && dpiX > 0)
                        dpi = (int)dpiX;
                }
                catch (DllNotFoundException)
                {
                }
                catch (EntryPointNotFoundException)
                {
                }

                return new MonitorBox(info.rcMonitor.Left, info.rcMonitor.Top, info.rcMonitor.Right, info.rcMonitor.Bottom, dpi);
            }
        }

        int virtualX = GetSystemMetrics(76);
        int virtualY = GetSystemMetrics(77);
        int virtualW = Math.Max(1, GetSystemMetrics(78));
        int virtualH = Math.Max(1, GetSystemMetrics(79));
        return new MonitorBox(virtualX, virtualY, virtualX + virtualW, virtualY + virtualH, 96);
    }

    static void EnsureClass()
    {
        if (_classReady)
            return;
        _module = GetModuleHandle(null);
        _whiteBrush = CreateSolidBrush(White);
        _accentBrush = CreateSolidBrush(Accent);
        var wc = new WNDCLASS
        {
            Style = 3,
            WndProc = ProcPtr,
            Instance = _module,
            ClassName = "FrameForgeRegionBorder"
        };
        RegisterClass(ref wc);
        _classReady = true;
    }

    static IntPtr OnMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WmDpiChanged && !_placing && Shown.TryGetValue(hwnd, out FrameBox frame))
        {
            KeepPhysicalSize(hwnd, frame);
            return IntPtr.Zero;
        }

        if (msg == WmEraseBkgnd)
        {
            Paint(hwnd, wParam);
            return (IntPtr)1;
        }

        if (msg == WmPaint)
        {
            IntPtr paint = Marshal.AllocHGlobal(80);
            try
            {
                IntPtr hdc = BeginPaint(hwnd, paint);
                Paint(hwnd, hdc);
                EndPaint(hwnd, paint);
            }
            finally
            {
                Marshal.FreeHGlobal(paint);
            }

            return IntPtr.Zero;
        }

        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    static void Paint(IntPtr hwnd, IntPtr hdc)
    {
        if (hdc == IntPtr.Zero)
            return;
        GetClientRect(hwnd, out RECT rect);
        int color = (int)GetUserData(hwnd);
        IntPtr brush = color == White ? _whiteBrush : _accentBrush;
        if (brush == IntPtr.Zero)
            brush = CreateSolidBrush(color == 0 ? Accent : color);
        FillRect(hdc, ref rect, brush);
        if (brush != _whiteBrush && brush != _accentBrush)
            DeleteObject(brush);
    }

    static void Destroy(ref IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return;
        Shown.Remove(hwnd);
        DestroyWindow(hwnd);
        hwnd = IntPtr.Zero;
    }

    static IntPtr CreateFrameRegion(int width, int height, int holeX, int holeY, int holeW, int holeH)
    {
        IntPtr outer = CreateRectRgn(0, 0, Math.Max(1, width), Math.Max(1, height));
        if (outer == IntPtr.Zero)
            return IntPtr.Zero;

        int left = Math.Clamp(holeX, 0, width);
        int top = Math.Clamp(holeY, 0, height);
        int right = Math.Clamp(holeX + holeW, 0, width);
        int bottom = Math.Clamp(holeY + holeH, 0, height);
        if (right - left < 1 || bottom - top < 1)
            return outer;

        IntPtr hole = CreateRectRgn(left, top, right, bottom);
        if (hole == IntPtr.Zero)
            return outer;

        int combined = CombineRgn(outer, outer, hole, 4);
        DeleteObject(hole);
        if (combined == 0)
        {
            DeleteObject(outer);
            return IntPtr.Zero;
        }

        return outer;
    }

    static void SetUserData(IntPtr hwnd, nint value)
    {
        if (IntPtr.Size == 8)
            SetWindowLongPtr64(hwnd, GwlUserData, value);
        else
            SetWindowLong32(hwnd, GwlUserData, value.ToInt32());
    }

    static nint GetUserData(IntPtr hwnd) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(hwnd, GwlUserData) : new nint(GetWindowLong32(hwnd, GwlUserData));

    readonly record struct FrameBox(int X, int Y, int Width, int Height, int HoleX, int HoleY, int HoleW, int HoleH);

    readonly record struct MonitorBox(int Left, int Top, int Right, int Bottom, int Dpi);

    delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WNDCLASS
    {
        public uint Style;
        public IntPtr WndProc;
        public int ClassExtra;
        public int WindowExtra;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr Background;
        public string? MenuName;
        public string ClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern ushort RegisterClass(ref WNDCLASS wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, int style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

    [DllImport("user32.dll")]
    static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);

    [DllImport("user32.dll")]
    static extern IntPtr MonitorFromRect(ref RECT rect, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [DllImport("user32.dll")]
    static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    static extern bool GetClientRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    static extern bool InvalidateRect(IntPtr hwnd, IntPtr rect, bool erase);

    [DllImport("user32.dll")]
    static extern bool UpdateWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    static extern IntPtr BeginPaint(IntPtr hwnd, IntPtr paint);

    [DllImport("user32.dll")]
    static extern bool EndPaint(IntPtr hwnd, IntPtr paint);

    [DllImport("user32.dll")]
    static extern int FillRect(IntPtr hdc, ref RECT rect, IntPtr brush);

    [DllImport("user32.dll")]
    static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("gdi32.dll")]
    static extern IntPtr CreateSolidBrush(int color);

    [DllImport("gdi32.dll")]
    static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    static extern int CombineRgn(IntPtr dest, IntPtr source1, IntPtr source2, int mode);

    [DllImport("gdi32.dll")]
    static extern bool DeleteObject(IntPtr obj);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr GetModuleHandle(string? module);

    [DllImport("shcore.dll")]
    static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
    static extern int SetWindowLong32(IntPtr hwnd, int index, int value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    static extern nint SetWindowLongPtr64(IntPtr hwnd, int index, nint value);

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    static extern int GetWindowLong32(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    static extern nint GetWindowLongPtr64(IntPtr hwnd, int index);
}
