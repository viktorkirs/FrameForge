using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using FrameForge.Core;

namespace FrameForge.Capture;

public sealed class WindowChoice
{
    public nint Handle { get; init; }
    public string Title { get; init; } = "";
    public string ProcessName { get; init; } = "";

    public string Label => string.IsNullOrWhiteSpace(ProcessName) ? Title : $"{Title} — {ProcessName}";

    public bool Matches(string? query) => string.IsNullOrWhiteSpace(query) ||
        query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).All(term =>
            Title.Contains(term, StringComparison.OrdinalIgnoreCase) ||
            ProcessName.Contains(term, StringComparison.OrdinalIgnoreCase));
}

public static class WindowCatalog
{
    public static bool IsAlive(nint hwnd) => hwnd != 0 && IsWindow(hwnd);

    public static bool IsMinimized(nint hwnd) => hwnd != 0 && IsIconic(hwnd);

    public static IReadOnlyList<WindowChoice> VisibleWindows()
    {
        var found = new List<WindowChoice>();
        int self = Environment.ProcessId;
        EnumWindows((hwnd, _) =>
        {
            if (found.Count >= 200)
                return false;
            if (!TryDescribe(hwnd, self, out WindowChoice? choice) || choice == null)
                return true;
            found.Add(choice);
            return true;
        }, 0);

        found.Sort((a, b) => string.Compare(a.Title, b.Title, StringComparison.CurrentCultureIgnoreCase));
        return found;
    }

    public static bool TryGetBounds(nint hwnd, out int x, out int y, out int width, out int height)
    {
        x = y = width = height = 0;
        if (!IsAlive(hwnd) || !GetWindowRect(hwnd, out NativeRect rect))
            return false;
        width = rect.Right - rect.Left;
        height = rect.Bottom - rect.Top;
        if (width < 2 || height < 2)
            return false;
        x = rect.Left;
        y = rect.Top;
        return true;
    }

    public static MonitorInfo? MonitorContaining(IReadOnlyList<MonitorInfo> monitors, nint hwnd)
    {
        if (!TryGetBounds(hwnd, out int x, out int y, out int width, out int height))
            return null;
        return BestOverlap(monitors, x, y, width, height, out _, out _, out _, out _);
    }

    public static bool TryIntersect(
        IReadOnlyList<MonitorInfo> monitors,
        int x,
        int y,
        int width,
        int height,
        out MonitorInfo monitor,
        out int cropX,
        out int cropY,
        out int cropWidth,
        out int cropHeight)
    {
        MonitorInfo? best = BestOverlap(monitors, x, y, width, height, out cropX, out cropY, out cropWidth, out cropHeight);
        if (best == null)
        {
            monitor = null!;
            return false;
        }

        monitor = best;
        return true;
    }

    static MonitorInfo? BestOverlap(
        IReadOnlyList<MonitorInfo> monitors,
        int x,
        int y,
        int width,
        int height,
        out int cropX,
        out int cropY,
        out int cropWidth,
        out int cropHeight)
    {
        cropX = cropY = cropWidth = cropHeight = 0;
        MonitorInfo? best = null;
        int bestArea = 0;
        foreach (MonitorInfo candidate in monitors)
        {
            int x1 = Math.Max(x, candidate.Left);
            int y1 = Math.Max(y, candidate.Top);
            int x2 = Math.Min(x + width, candidate.Left + candidate.Width);
            int y2 = Math.Min(y + height, candidate.Top + candidate.Height);
            int overlapWidth = x2 - x1;
            int overlapHeight = y2 - y1;
            if (overlapWidth < 16 || overlapHeight < 16)
                continue;

            int area = overlapWidth * overlapHeight;
            if (area <= bestArea)
                continue;

            int alignedX = (x1 - candidate.Left) & ~1;
            int alignedY = (y1 - candidate.Top) & ~1;
            int alignedWidth = Math.Min(candidate.Width - alignedX, overlapWidth) & ~1;
            int alignedHeight = Math.Min(candidate.Height - alignedY, overlapHeight) & ~1;
            if (alignedWidth < 16 || alignedHeight < 16)
                continue;

            best = candidate;
            bestArea = area;
            cropX = alignedX;
            cropY = alignedY;
            cropWidth = alignedWidth;
            cropHeight = alignedHeight;
        }

        return best;
    }

    static bool TryDescribe(nint hwnd, int selfProcess, out WindowChoice? choice)
    {
        choice = null;
        if (!IsWindowVisible(hwnd))
            return false;
        if (GetAncestor(hwnd, 2) != hwnd)
            return false;

        nint style = GetWindowLongPtr(hwnd, -20);
        const nint tool = 0x00000080;
        const nint app = 0x00040000;
        if ((style & tool) != 0 && (style & app) == 0)
            return false;

        if (DwmGetWindowAttribute(hwnd, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
            return false;

        int length = GetWindowTextLength(hwnd);
        if (length <= 0)
            return false;

        var title = new StringBuilder(length + 1);
        GetWindowText(hwnd, title, title.Capacity);
        string text = title.ToString().Trim();
        if (text.Length == 0)
            return false;

        GetWindowThreadProcessId(hwnd, out uint processId);
        if ((int)processId == selfProcess)
            return false;

        string process = "";
        try
        {
            using Process owner = Process.GetProcessById((int)processId);
            process = owner.ProcessName;
        }
        catch
        {
            process = "";
        }

        choice = new WindowChoice
        {
            Handle = hwnd,
            Title = text,
            ProcessName = process
        };
        return true;
    }

    static nint GetWindowLongPtr(nint hwnd, int index) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(hwnd, index) : new nint(GetWindowLong32(hwnd, index));

    delegate bool EnumProc(nint hwnd, nint lParam);

    [DllImport("user32.dll")]
    static extern bool EnumWindows(EnumProc callback, nint lParam);

    [DllImport("user32.dll")]
    static extern bool IsWindow(nint hwnd);

    [DllImport("user32.dll")]
    static extern bool IsWindowVisible(nint hwnd);

    [DllImport("user32.dll")]
    static extern bool IsIconic(nint hwnd);

    [DllImport("user32.dll")]
    static extern nint GetAncestor(nint hwnd, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    static extern int GetWindowLong32(nint hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    static extern nint GetWindowLongPtr64(nint hwnd, int index);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetWindowText(nint hwnd, StringBuilder text, int count);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetWindowTextLength(nint hwnd);

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [DllImport("user32.dll")]
    static extern bool GetWindowRect(nint hwnd, out NativeRect rect);

    [DllImport("dwmapi.dll")]
    static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);

    [StructLayout(LayoutKind.Sequential)]
    struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
