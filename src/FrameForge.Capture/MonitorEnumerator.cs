using System.Runtime.InteropServices;
using FrameForge.Core;
using SharpGen.Runtime;
using Vortice.DXGI;

namespace FrameForge.Capture;

public static class MonitorEnumerator
{
    public static IReadOnlyList<MonitorInfo> GetMonitors()
    {
        var found = new List<MonitorInfo>();
        using IDXGIFactory1 factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint adapterIndex = 0; ; adapterIndex++)
        {
            Result adapterResult = factory.EnumAdapters1(adapterIndex, out IDXGIAdapter1? adapter);
            if (adapterResult.Failure || adapter == null)
                break;

            using (adapter)
            {
                string adapterName = adapter.Description1.Description?.Trim() ?? "GPU";
                for (uint outputIndex = 0; ; outputIndex++)
                {
                    Result outputResult = adapter.EnumOutputs(outputIndex, out IDXGIOutput? output);
                    if (outputResult.Failure || output == null)
                        break;

                    using (output)
                    {
                        OutputDescription description = output.Description;
                        if (!description.AttachedToDesktop || description.Monitor == 0)
                            continue;

                        bool primary = IsPrimary(description.Monitor);
                        Vortice.RawRect bounds = description.DesktopCoordinates;
                        found.Add(new MonitorInfo
                        {
                            IsPrimary = primary,
                            Handle = description.Monitor,
                            DeviceName = description.DeviceName ?? "",
                            AdapterName = adapterName,
                            Width = Math.Max(0, bounds.Right - bounds.Left),
                            Height = Math.Max(0, bounds.Bottom - bounds.Top),
                            Left = bounds.Left,
                            Top = bounds.Top
                        });
                    }
                }
            }
        }

        found.Sort((a, b) =>
        {
            int primary = b.IsPrimary.CompareTo(a.IsPrimary);
            if (primary != 0)
                return primary;
            int top = a.Top.CompareTo(b.Top);
            return top != 0 ? top : a.Left.CompareTo(b.Left);
        });

        for (int i = 0; i < found.Count; i++)
        {
            MonitorInfo source = found[i];
            found[i] = new MonitorInfo
            {
                Index = i + 1,
                IsPrimary = source.IsPrimary,
                Handle = source.Handle,
                DeviceName = source.DeviceName,
                AdapterName = source.AdapterName,
                Width = source.Width,
                Height = source.Height,
                Left = source.Left,
                Top = source.Top
            };
        }

        return found;
    }

    public static MonitorInfo Select(IReadOnlyList<MonitorInfo> monitors, string? deviceName)
    {
        if (monitors.Count == 0)
            throw new InvalidOperationException(Loc.Get("NoMonitorFound"));

        if (!string.IsNullOrWhiteSpace(deviceName))
        {
            MonitorInfo? match = monitors.FirstOrDefault(monitor =>
                string.Equals(monitor.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase));
            if (match != null)
                return match;
        }

        return monitors.FirstOrDefault(monitor => monitor.IsPrimary) ?? monitors[0];
    }

    static bool IsPrimary(nint monitor)
    {
        var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
        return GetMonitorInfo(monitor, ref info) && (info.Flags & 1) != 0;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool GetMonitorInfo(nint monitor, ref MonitorInfoEx info);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct MonitorInfoEx
    {
        public int Size;
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
        public int WorkLeft;
        public int WorkTop;
        public int WorkRight;
        public int WorkBottom;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
    }
}
