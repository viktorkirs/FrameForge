using System.Runtime.InteropServices;
using Vortice.MediaFoundation;

namespace FrameForge.Encoding;

public static class HardwareEncoderCatalog
{
    const int Yes = 1;
    const int No = 2;
    const int Failed = 3;

    static readonly object Gate = new();
    static int _state;

    public static bool? CachedH264
    {
        get
        {
            lock (Gate)
            {
                return _state switch
                {
                    Yes => true,
                    No => false,
                    _ => null
                };
            }
        }
    }

    public static bool TryProbe(out bool available)
    {
        lock (Gate)
        {
            if (_state is Yes or No or Failed)
            {
                available = _state == Yes;
                return _state != Failed;
            }

            try
            {
                available = EnumerateHardwareH264();
                _state = available ? Yes : No;
                return true;
            }
            catch
            {
                available = false;
                _state = Failed;
                return false;
            }
        }
    }

    static bool EnumerateHardwareH264()
    {
        MediaFoundationStartup.Ensure();
        uint flags = (uint)EnumFlag.EnumFlagHardware
            | (uint)EnumFlag.EnumFlagSyncmft
            | (uint)EnumFlag.EnumFlagAsyncmft
            | (uint)EnumFlag.EnumFlagLocalmft;
        var output = new RegisterTypeInfo
        {
            GuidMajorType = MediaTypeGuids.Video,
            GuidSubtype = VideoFormatGuids.H264
        };
        MediaFactory.MFTEnumEx(TransformCategoryGuids.VideoEncoder, flags, null, output, out IntPtr array, out uint count);
        try
        {
            return count > 0;
        }
        finally
        {
            FreeActivates(array, count);
        }
    }

    static void FreeActivates(IntPtr array, uint count)
    {
        if (array == IntPtr.Zero)
            return;

        try
        {
            int limit = (int)Math.Min(count, 64);
            for (int i = 0; i < limit; i++)
            {
                IntPtr item = Marshal.ReadIntPtr(array, i * IntPtr.Size);
                if (item != IntPtr.Zero)
                    Marshal.Release(item);
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(array);
        }
    }
}
