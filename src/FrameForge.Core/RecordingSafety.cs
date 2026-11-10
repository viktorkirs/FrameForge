using System.Runtime.InteropServices;

namespace FrameForge.Core;

public static class RecordingSafety
{
    public const long LowRamBytes = 4L * 1024 * 1024 * 1024;
    public const long DiskStopBytes = 500L * 1024 * 1024;
    public const int LowDiskMinutes = 10;

    public static bool Is4K(int width, int height)
    {
        if (width <= 0 || height <= 0)
            return false;
        if (width >= 3200 || height >= 2000)
            return true;
        return (long)width * height >= 7_000_000;
    }

    public static int BitsPerSecond(AppSettings settings, int width, int height)
    {
        if (width < 2 || height < 2 || settings.Fps <= 0)
            return 0;
        return settings.AutoBitrate
            ? VideoBitrate.Compute(settings.Quality, width, height, settings.Fps)
            : settings.ManualBitrateMbps * 1_000_000;
    }

    public static int MinutesThatFit(long freeBytes, int bitrateBitsPerSecond)
    {
        if (freeBytes <= 0 || bitrateBitsPerSecond <= 0)
            return 0;
        double minutes = freeBytes * 8d / bitrateBitsPerSecond / 60d;
        if (minutes < 1)
            return 0;
        if (minutes >= 100_000)
            return 100_000;
        return (int)Math.Floor(minutes);
    }

    public static bool DiskIsLow(int minutes) => minutes < LowDiskMinutes;

    public static string? RiskKey(bool? softwareEncoder, int width, int height, int fps, long availableRamBytes)
    {
        bool fourK = Is4K(width, height);
        bool highRate = fps >= 60;
        bool softwareRisk = softwareEncoder == true && (fourK || highRate);
        bool ramRisk = availableRamBytes is > 0 and < LowRamBytes && fourK && highRate;
        if (softwareRisk && ramRisk)
            return "RiskSoftwareRam";
        if (softwareRisk)
            return "RiskSoftware";
        if (ramRisk)
            return "RiskLowRam";
        return null;
    }

    public static long AvailableMemory()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref status) ? (long)status.AvailPhys : -1;
    }

    public static long AvailableDisk(string? path)
    {
        try
        {
            string root = Path.GetPathRoot(Resolve(path)) ?? "";
            if (root.Length == 0)
                return -1;
            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch
        {
            return -1;
        }
    }

    public static string DriveLabel(string? path)
    {
        try
        {
            string root = Path.GetPathRoot(Resolve(path)) ?? "";
            return root.TrimEnd('\\', '/');
        }
        catch
        {
            return "";
        }
    }

    static string Resolve(string? path)
    {
        string full = string.IsNullOrWhiteSpace(path) ? AppPaths.DefaultOutputDirectory : path;
        return Path.GetFullPath(full);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
