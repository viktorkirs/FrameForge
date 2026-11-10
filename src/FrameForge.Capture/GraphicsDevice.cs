using System.Runtime.InteropServices;
using FrameForge.Core;
using FrameForge.Diagnostics;
using WinRT;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.DirectX.Direct3D11;

namespace FrameForge.Capture;

public sealed class GraphicsDevice : IDisposable
{
    GraphicsDevice(ID3D11Device device, ID3D11DeviceContext context, IDirect3DDevice winRtDevice, string adapterName)
    {
        Device = device;
        Context = context;
        WinRtDevice = winRtDevice;
        AdapterName = adapterName;
    }

    public ID3D11Device Device { get; }
    public ID3D11DeviceContext Context { get; }
    public IDirect3DDevice WinRtDevice { get; }
    public string AdapterName { get; }

    public static GraphicsDevice CreateForMonitor(MonitorHandle monitor)
    {
        using IDXGIFactory1 factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        IDXGIAdapter1? matched = null;
        string adapterName = "GPU";
        try
        {
            for (uint adapterIndex = 0; matched == null; adapterIndex++)
            {
                Result adapterResult = factory.EnumAdapters1(adapterIndex, out IDXGIAdapter1? adapter);
                if (adapterResult.Failure || adapter == null)
                    break;

                bool found = false;
                for (uint outputIndex = 0; ; outputIndex++)
                {
                    Result outputResult = adapter.EnumOutputs(outputIndex, out IDXGIOutput? output);
                    if (outputResult.Failure || output == null)
                        break;

                    using (output)
                    {
                        if (output.Description.Monitor == monitor.Handle)
                            found = true;
                    }
                }

                if (found)
                {
                    matched = adapter;
                    adapterName = adapter.Description1.Description?.Trim() ?? adapterName;
                }
                else
                {
                    adapter.Dispose();
                }
            }

            FeatureLevel[] levels =
            [
                FeatureLevel.Level_11_1,
                FeatureLevel.Level_11_0
            ];
            nint adapterPointer = matched?.NativePointer ?? 0;
            DriverType driver = matched == null ? DriverType.Hardware : DriverType.Unknown;
            D3D11.D3D11CreateDevice(
                adapterPointer,
                driver,
                DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
                levels,
                out ID3D11Device? device,
                out ID3D11DeviceContext? context).CheckError();

            if (device == null || context == null)
                throw new InvalidOperationException(Loc.Get("DeviceFailed"));

            try
            {
                using ID3D11Multithread multithread = device.QueryInterface<ID3D11Multithread>();
                multithread.SetMultithreadProtected(true);

                using IDXGIDevice dxgiDevice = device.QueryInterface<IDXGIDevice>();
                IDirect3DDevice winRtDevice = Direct3DInterop.CreateWinRtDevice(dxgiDevice);
                var created = new GraphicsDevice(device, context, winRtDevice, adapterName);
                device = null!;
                context = null!;
                Log.Info($"D3D11 device: {adapterName}");
                return created;
            }
            catch
            {
                context?.Dispose();
                device?.Dispose();
                throw;
            }
        }
        finally
        {
            matched?.Dispose();
        }
    }

    public void Dispose()
    {
        try { WinRtDevice.Dispose(); } catch { /* A failed WinRT wrapper must not take down the process. */ }
        Context.Dispose();
        Device.Dispose();
    }
}

static class Direct3DInterop
{
    public static IDirect3DDevice CreateWinRtDevice(IDXGIDevice dxgiDevice)
    {
        int hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out nint graphicsDevice);
        if (hr < 0)
            Marshal.ThrowExceptionForHR(hr);
        return MarshalInterface<IDirect3DDevice>.FromAbi(graphicsDevice);
    }

    [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice", ExactSpelling = true)]
    static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);
}

public readonly record struct MonitorHandle(nint Handle);
