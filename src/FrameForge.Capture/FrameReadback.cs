using System.Runtime.InteropServices;
using FrameForge.Core;
using SharpGen.Runtime;
using Vortice.Direct3D11;

namespace FrameForge.Capture;

/// <summary>Owns one reusable staging texture and output buffer. Read overwrites the previous frame.</summary>
public sealed class FrameReadback : IDisposable
{
    readonly GraphicsDevice _device;
    readonly BgraFrame _frame;
    ID3D11Texture2D? _staging;
    bool _disposed;

    public FrameReadback(GraphicsDevice device, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        _device = device;
        _frame = new BgraFrame { Width = width, Height = height, Pixels = new byte[checked(width * height * 4)] };
    }

    public unsafe BgraFrame Read(ID3D11Texture2D source, ScreenRect crop, bool preserveAspect = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Texture2DDescription description = source.Description;
        if (crop.X < 0 || crop.Y < 0 || crop.Width <= 0 || crop.Height <= 0 ||
            (long)crop.X + crop.Width > description.Width || (long)crop.Y + crop.Height > description.Height)
            throw new ArgumentOutOfRangeException(nameof(crop));
        if (_staging == null || _staging.Description.Width != crop.Width || _staging.Description.Height != crop.Height)
        {
            description.Width = (uint)crop.Width;
            description.Height = (uint)crop.Height;
            description.BindFlags = BindFlags.None;
            description.Usage = ResourceUsage.Staging;
            description.CPUAccessFlags = CpuAccessFlags.Read;
            description.MiscFlags = ResourceOptionFlags.None;
            ID3D11Texture2D replacement = _device.Device.CreateTexture2D(description);
            _staging?.Dispose();
            _staging = replacement;
        }
        ID3D11DeviceContext context = _device.Context;
        context.CopySubresourceRegion(_staging, 0, 0, 0, 0, source, 0,
            new Vortice.Mathematics.Box(crop.X, crop.Y, 0, crop.X + crop.Width, crop.Y + crop.Height, 1));
        ScreenRect destination = preserveAspect ? FrameLayout.Fit(crop.Width, crop.Height, _frame.Width, _frame.Height)
            : new ScreenRect(0, 0, _frame.Width, _frame.Height);
        if (destination.Width != _frame.Width || destination.Height != _frame.Height)
            MemoryMarshal.Cast<byte, uint>(_frame.Pixels.AsSpan()).Fill(0xff000000);
        context.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None, out MappedSubresource mapped).CheckError();
        try
        {
            fixed (byte* output = _frame.Pixels)
            {
                byte* input = (byte*)mapped.DataPointer;
                for (int y = 0; y < destination.Height; y++)
                {
                    uint* row = (uint*)(input + (nint)(y * (long)crop.Height / destination.Height) * mapped.RowPitch);
                    uint* target = (uint*)output + (destination.Y + y) * _frame.Width + destination.X;
                    if (destination.Width == crop.Width)
                        new ReadOnlySpan<uint>(row, crop.Width).CopyTo(new Span<uint>(target, crop.Width));
                    else
                        for (int x = 0; x < destination.Width; x++) target[x] = row[x * (long)crop.Width / destination.Width];
                }
            }
        }
        finally { context.Unmap(_staging, 0); }
        return _frame;
    }

    public void Dispose() { if (_disposed) return; _disposed = true; _staging?.Dispose(); }
}
