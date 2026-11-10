using System.Runtime.InteropServices;
using FrameForge.Core;
using FrameForge.Diagnostics;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Windows.Foundation.Metadata;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace FrameForge.Capture;

sealed class WgcCapture : IDisposable
{
    readonly GraphicsDevice _device;
    readonly Direct3D11CaptureFramePool _pool;
    readonly GraphicsCaptureSession _session;
    readonly GraphicsCaptureItem _item;
    ID3D11Texture2D _latest;
    SizeInt32 _poolSize;
    readonly object _gate = new();
    readonly ManualResetEventSlim _firstFrame = new(false);
    int _width;
    int _height;
    long _generation;
    int _frameErrors;
    int _closed;
    bool _disposed;

    WgcCapture(GraphicsDevice device, GraphicsCaptureItem item, Direct3D11CaptureFramePool pool, GraphicsCaptureSession session, ID3D11Texture2D latest, int width, int height, SizeInt32 poolSize)
    {
        _device = device;
        _item = item;
        _pool = pool;
        _session = session;
        _latest = latest;
        _poolSize = poolSize;
        _width = width;
        _height = height;
        _item.Closed += OnItemClosed;
        _pool.FrameArrived += OnFrameArrived;
        _session.StartCapture();
    }

    public int Width => _width;
    public int Height => _height;
    public long Generation => Interlocked.Read(ref _generation);
    public bool IsClosed => Volatile.Read(ref _closed) != 0;

    public static WgcCapture Start(GraphicsDevice device, MonitorInfo monitor, bool captureCursor, int fps)
    {
        GraphicsCaptureItem item = CaptureInterop.CreateForMonitor(monitor.Handle);
        return StartItem(device, item, captureCursor, fps, "monitor");
    }

    public static WgcCapture StartWindow(GraphicsDevice device, nint window, bool captureCursor, int fps)
    {
        if (!WindowCatalog.IsAlive(window))
            throw new InvalidOperationException(Loc.Get("WindowGone"));
        if (WindowCatalog.IsMinimized(window))
            throw new InvalidOperationException(Loc.Get("WindowMinimized"));

        GraphicsCaptureItem item = CaptureInterop.CreateForWindow(window);
        if (item.Size.Width < 2 || item.Size.Height < 2)
            throw new InvalidOperationException(Loc.Get("WindowMinimized"));
        return StartItem(device, item, captureCursor, fps, "window");
    }

    static WgcCapture StartItem(GraphicsDevice device, GraphicsCaptureItem item, bool captureCursor, int fps, string kind)
    {
        SizeInt32 poolSize = item.Size;
        int width = Math.Max(2, poolSize.Width & ~1);
        int height = Math.Max(2, poolSize.Height & ~1);
        Direct3D11CaptureFramePool pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            device.WinRtDevice,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            3,
            poolSize);
        GraphicsCaptureSession session = pool.CreateCaptureSession(item);
        session.IsCursorCaptureEnabled = captureCursor;
        if (ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", "IsBorderRequired"))
            session.IsBorderRequired = false;

        ID3D11Texture2D latest = device.Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Vortice.DXGI.Format.B8G8R8A8_UNorm,
            SampleDescription = new Vortice.DXGI.SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None
        });

        var capture = new WgcCapture(device, item, pool, session, latest, width, height, poolSize);
        Log.Info($"WGC started {kind} {width}x{height} cursor={captureCursor} target={fps}fps");
        return capture;
    }

    void OnItemClosed(GraphicsCaptureItem sender, object args) => Volatile.Write(ref _closed, 1);

    public bool WaitForFrame(TimeSpan timeout) => _firstFrame.Wait(timeout);

    public long CopyLatest(ref ID3D11Texture2D? destination)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (destination == null || destination.Description.Width != _width || destination.Description.Height != _height)
            {
                ID3D11Texture2D replacement = _device.Device.CreateTexture2D(_latest.Description);
                destination?.Dispose();
                destination = replacement;
            }
            _device.Context.CopyResource(destination, _latest);
            return _generation;
        }
    }

    void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        lock (_gate)
        {
            if (_disposed) return;
            try
            {
                Direct3D11CaptureFrame? latest = null;
                while (true)
                {
                    Direct3D11CaptureFrame? frame = sender.TryGetNextFrame();
                    if (frame == null)
                        break;
                    latest?.Dispose();
                    latest = frame;
                }

                if (latest == null)
                    return;

                SizeInt32 size = latest.ContentSize;
                if (size.Width < 2 || size.Height < 2) { latest.Dispose(); return; }
                if (size.Width != _poolSize.Width || size.Height != _poolSize.Height)
                {
                    // The first frame announcing a larger size still has the old pool's
                    // surface. Retire it before Recreate and wait for a complete new frame.
                    latest.Dispose();
                    sender.Recreate(_device.WinRtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 3, size);
                    _poolSize = size;
                    return;
                }
                using (latest)
                {
                    int contentWidth = size.Width & ~1;
                    int contentHeight = size.Height & ~1;
                    using ID3D11Texture2D source = CaptureInterop.GetTexture(latest.Surface);
                    if (source.Description.Width < contentWidth || source.Description.Height < contentHeight) return;
                    ID3D11Texture2D? replacement = null;
                    if (_width != contentWidth || _height != contentHeight)
                    {
                        Texture2DDescription description = _latest.Description;
                        description.Width = (uint)contentWidth;
                        description.Height = (uint)contentHeight;
                        replacement = _device.Device.CreateTexture2D(description);
                    }
                    try
                    {
                        _device.Context.CopySubresourceRegion(
                            replacement ?? _latest,
                            0,
                            0,
                            0,
                            0,
                            source,
                            0,
                            new Vortice.Mathematics.Box(0, 0, 0, contentWidth, contentHeight, 1));
                        if (replacement != null)
                        {
                            _latest.Dispose();
                            _latest = replacement;
                            replacement = null;
                            _width = contentWidth;
                            _height = contentHeight;
                            Log.Info($"WGC resized {contentWidth}x{contentHeight}");
                        }
                    }
                    finally { replacement?.Dispose(); }
                }

                Interlocked.Increment(ref _generation);
                _firstFrame.Set();
            }
            catch (Exception ex)
            {
                if (Interlocked.Increment(ref _frameErrors) <= 3)
                    Log.Error("Ошибка кадра захвата.", ex);
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _item.Closed -= OnItemClosed;
            _pool.FrameArrived -= OnFrameArrived;
        }
        _session.Dispose();
        _pool.Dispose();
        _latest.Dispose();
        _firstFrame.Dispose();
    }
}

static class CaptureInterop
{
    static readonly Guid ItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    static readonly Guid TextureIid = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    public static GraphicsCaptureItem CreateForWindow(nint window)
    {
        IGraphicsCaptureItemInterop interop = ActivationFactory();
        Guid itemIid = ItemIid;
        int hr = interop.CreateForWindow(window, ref itemIid, out nint item);
        Marshal.ThrowExceptionForHR(hr);
        return GraphicsCaptureItem.FromAbi(item);
    }

    public static GraphicsCaptureItem CreateForMonitor(nint monitor)
    {
        IGraphicsCaptureItemInterop interop = ActivationFactory();
        Guid itemIid = ItemIid;
        int hr = interop.CreateForMonitor(monitor, ref itemIid, out nint item);
        Marshal.ThrowExceptionForHR(hr);
        return GraphicsCaptureItem.FromAbi(item);
    }

    static IGraphicsCaptureItemInterop ActivationFactory()
    {
        const string className = "Windows.Graphics.Capture.GraphicsCaptureItem";
        Guid factoryIid = typeof(IGraphicsCaptureItemInterop).GUID;
        int hr = WindowsCreateString(className, className.Length, out nint classId);
        Marshal.ThrowExceptionForHR(hr);
        try
        {
            hr = RoGetActivationFactory(classId, ref factoryIid, out nint factoryPointer);
            Marshal.ThrowExceptionForHR(hr);
            var interop = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factoryPointer);
            Marshal.Release(factoryPointer);
            return interop;
        }
        finally
        {
            WindowsDeleteString(classId);
        }
    }

    public static ID3D11Texture2D GetTexture(IDirect3DSurface surface)
    {
        IDxgiSurfaceAccess access = WinRT.CastExtensions.As<IDxgiSurfaceAccess>(surface);
        Guid textureId = TextureIid;
        int hr = access.GetInterface(ref textureId, out nint pointer);
        Marshal.ThrowExceptionForHR(hr);
        return new ID3D11Texture2D(pointer);
    }

    [System.Runtime.InteropServices.ComImport]
    [System.Runtime.InteropServices.Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    [System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
    interface IDxgiSurfaceAccess
    {
        [PreserveSig]
        int GetInterface(ref Guid iid, out nint result);
    }

    [DllImport("combase.dll")]
    static extern int RoGetActivationFactory(nint classId, ref Guid iid, out nint factory);

    [DllImport("combase.dll")]
    static extern int WindowsCreateString([MarshalAs(UnmanagedType.LPWStr)] string source, int length, out nint hstring);

    [DllImport("combase.dll")]
    static extern int WindowsDeleteString(nint hstring);

    [System.Runtime.InteropServices.ComImport]
    [System.Runtime.InteropServices.Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
    interface IGraphicsCaptureItemInterop
    {
        [PreserveSig]
        int CreateForWindow(nint window, ref Guid iid, out nint result);

        [PreserveSig]
        int CreateForMonitor(nint monitor, ref Guid iid, out nint result);
    }
}
