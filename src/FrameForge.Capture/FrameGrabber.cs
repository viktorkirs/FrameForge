using FrameForge.Core;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace FrameForge.Capture;

public static class FrameGrabber
{
    public static BgraFrame Capture(MonitorInfo monitor, int maxEdge = 0) =>
        Capture(new CaptureRequest { Monitor = monitor }, maxEdge);

    public static BgraFrame Capture(CaptureRequest request, int maxEdge = 0)
    {
        using GraphicsDevice device = GraphicsDevice.CreateForMonitor(new MonitorHandle(request.Monitor.Handle));
        using WgcCapture capture = request.Window != 0
            ? WgcCapture.StartWindow(device, request.Window, captureCursor: true, fps: 30)
            : WgcCapture.Start(device, request.Monitor, captureCursor: true, fps: 30);
        if (!capture.WaitForFrame(TimeSpan.FromSeconds(2)))
            throw new TimeoutException(Loc.Get("GrabFailed"));

        // The first delivered frame is sometimes empty. Prefer the next one.
        long seen = capture.Generation;
        var waited = TimeSpan.Zero;
        while (capture.Generation < seen + 1 && waited < TimeSpan.FromMilliseconds(400))
        {
            Thread.Sleep(20);
            waited += TimeSpan.FromMilliseconds(20);
        }

        ID3D11Texture2D? snapshot = null;
        try
        {
            capture.CopyLatest(ref snapshot);
            int width = (int)snapshot!.Description.Width;
            int height = (int)snapshot.Description.Height;
            if (request.Window == 0 && request.HasCrop)
            {
                if (request.CropX < 0 || request.CropY < 0 ||
                    (long)request.CropX + request.CropWidth > width ||
                    (long)request.CropY + request.CropHeight > height)
                    throw new InvalidOperationException(Loc.Get("RegionOffScreen"));
                return ReadBox(device, snapshot, request.CropX, request.CropY, request.CropWidth, request.CropHeight, maxEdge);
            }
            return ReadTexture(device, snapshot, width, height, maxEdge);
        }
        finally { snapshot?.Dispose(); }
    }

    public static BgraFrame ReadTexture(GraphicsDevice device, ID3D11Texture2D source, int width, int height, int maxEdge)
    {
        using ID3D11Texture2D staging = device.Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None
        });

        device.Context.CopyResource(staging, source);
        return PixelCopy.FromStaging(device.Context, staging, width, height, maxEdge);
    }

    public static BgraFrame ReadBox(GraphicsDevice device, ID3D11Texture2D source, int x, int y, int width, int height, int maxEdge)
    {
        using ID3D11Texture2D staging = device.Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None
        });

        device.Context.CopySubresourceRegion(
            staging,
            0,
            0,
            0,
            0,
            source,
            0,
            new Vortice.Mathematics.Box(x, y, 0, x + width, y + height, 1));
        return PixelCopy.FromStaging(device.Context, staging, width, height, maxEdge);
    }
}

static class PixelCopy
{
    public static unsafe BgraFrame FromStaging(ID3D11DeviceContext context, ID3D11Texture2D staging, int width, int height, int maxEdge)
    {
        (int destinationWidth, int destinationHeight) = Fit(width, height, maxEdge);
        var pixels = new byte[destinationWidth * destinationHeight * 4];
        context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None, out MappedSubresource mapped).CheckError();
        try
        {
            byte* source = (byte*)mapped.DataPointer;
            int stride = (int)mapped.RowPitch;
            if (destinationWidth == width && destinationHeight == height)
            {
                for (int y = 0; y < height; y++)
                    new ReadOnlySpan<byte>(source + (y * stride), width * 4).CopyTo(pixels.AsSpan(y * width * 4));
            }
            else
            {
                for (int y = 0; y < destinationHeight; y++)
                {
                    int sourceY = y * height / destinationHeight;
                    for (int x = 0; x < destinationWidth; x++)
                    {
                        int sourceX = x * width / destinationWidth;
                        int from = (sourceY * stride) + (sourceX * 4);
                        int to = ((y * destinationWidth) + x) * 4;
                        pixels[to] = source[from];
                        pixels[to + 1] = source[from + 1];
                        pixels[to + 2] = source[from + 2];
                        pixels[to + 3] = source[from + 3];
                    }
                }
            }
        }
        finally
        {
            context.Unmap(staging, 0);
        }

        return new BgraFrame
        {
            Width = destinationWidth,
            Height = destinationHeight,
            Pixels = pixels
        };
    }

    public static (int Width, int Height) Fit(int width, int height, int maxEdge)
    {
        if (maxEdge <= 0 || (width <= maxEdge && height <= maxEdge))
            return (width, height);

        double scale = maxEdge / (double)Math.Max(width, height);
        return (Math.Max(2, (int)(width * scale)), Math.Max(2, (int)(height * scale)));
    }
}
