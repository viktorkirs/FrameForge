using System.Runtime.InteropServices;
using FrameForge.Core;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace FrameForge.Capture;

sealed class GpuScaler : IDisposable
{
    const string ShaderSource = """
        cbuffer Crop : register(b0)
        {
            float2 uvOrigin;
            float2 uvExtent;
        };

        Texture2D source : register(t0);
        SamplerState linearSampler : register(s0);

        struct Vertex { float4 position : SV_Position; float2 uv : TEXCOORD0; };

        Vertex VS(uint id : SV_VertexID)
        {
            float2 uv = float2((id << 1) & 2, id & 2);
            Vertex output;
            output.position = float4(uv * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0);
            output.uv = uvOrigin + uv * uvExtent;
            return output;
        }

        float4 PS(Vertex input) : SV_Target
        {
            return source.Sample(linearSampler, input.uv);
        }
        """;

    readonly GraphicsDevice _device;
    readonly ID3D11VertexShader _vertexShader;
    readonly ID3D11PixelShader _pixelShader;
    ID3D11ShaderResourceView _sourceView;
    readonly ID3D11SamplerState _sampler;
    readonly Slot[] _slots;
    readonly bool[] _busy;
    readonly object _gate = new();
    readonly int _outputWidth;
    readonly int _outputHeight;
    int _inputWidth;
    int _inputHeight;
    readonly bool _preserveAspect;
    (int X, int Y, int Width, int Height) _crop;
    readonly ID3D11Buffer _cropBuffer;
    readonly ID3D11Buffer[] _cropSlot;

    public GpuScaler(
        GraphicsDevice device,
        int inputWidth,
        int inputHeight,
        int outputWidth,
        int outputHeight,
        BindFlags extraBindFlags,
        int cropX = 0,
        int cropY = 0,
        int cropWidth = 0,
        int cropHeight = 0,
        bool preserveAspect = false)
    {
        _device = device;
        _outputWidth = outputWidth;
        _outputHeight = outputHeight;
        _inputWidth = inputWidth;
        _inputHeight = inputHeight;
        _preserveAspect = preserveAspect;
        if (cropWidth <= 0 || cropHeight <= 0)
        {
            cropX = 0;
            cropY = 0;
            cropWidth = inputWidth;
            cropHeight = inputHeight;
        }

        _vertexShader = device.Device.CreateVertexShader(ShaderCompiler.Compile(ShaderSource, "VS", "vs_5_0"));
        _pixelShader = device.Device.CreatePixelShader(ShaderCompiler.Compile(ShaderSource, "PS", "ps_5_0"));
        _cropBuffer = device.Device.CreateBuffer(new BufferDescription
        {
            ByteWidth = 16,
            Usage = ResourceUsage.Dynamic,
            BindFlags = BindFlags.ConstantBuffer,
            CPUAccessFlags = CpuAccessFlags.Write,
            MiscFlags = ResourceOptionFlags.None
        });
        _cropSlot = [_cropBuffer];
        UpdateCrop(cropX, cropY, cropWidth, cropHeight);

        Source = device.Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)inputWidth,
            Height = (uint)inputHeight,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None
        });
        _sourceView = device.Device.CreateShaderResourceView(Source);
        _sampler = device.Device.CreateSamplerState(new SamplerDescription
        {
            Filter = Filter.MinMagLinearMipPoint,
            AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp,
            ComparisonFunc = ComparisonFunction.Never,
            MaxAnisotropy = 1,
            MinLOD = 0,
            MaxLOD = float.MaxValue
        });

        BindFlags bindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource | extraBindFlags;
        _slots = new Slot[8];
        _busy = new bool[8];
        for (int i = 0; i < _slots.Length; i++)
        {
            ID3D11Texture2D texture = device.Device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)outputWidth,
                Height = (uint)outputHeight,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = bindFlags,
                CPUAccessFlags = CpuAccessFlags.None,
                MiscFlags = ResourceOptionFlags.None
            });
            ID3D11RenderTargetView view = device.Device.CreateRenderTargetView(texture);
            _slots[i] = new Slot(texture, view);
        }
    }

    public ID3D11Texture2D Source { get; private set; }
    public int InputWidth => _inputWidth;
    public int InputHeight => _inputHeight;

    public void ResizeInput(int width, int height)
    {
        if (width == _inputWidth && height == _inputHeight) return;
        Texture2DDescription description = Source.Description;
        description.Width = (uint)width;
        description.Height = (uint)height;
        ID3D11Texture2D replacement = _device.Device.CreateTexture2D(description);
        ID3D11ShaderResourceView view;
        try { view = _device.Device.CreateShaderResourceView(replacement); }
        catch { replacement.Dispose(); throw; }
        _sourceView.Dispose();
        Source.Dispose();
        Source = replacement;
        _sourceView = view;
        _inputWidth = width;
        _inputHeight = height;
        _crop = default; // UVs depend on the input size even when the crop rectangle is unchanged.
        // Keep the eight encoder-facing surfaces and their in-flight leases intact.
    }

    public void UpdateCrop(int x, int y, int width, int height)
    {
        if (_crop == (x, y, width, height))
            return;
        WriteCrop(_device.Context, _cropBuffer, x, y, width, height, _inputWidth, _inputHeight);
        _crop = (x, y, width, height);
    }

    public bool TryConvert(out ID3D11Texture2D? texture, out Action? release)
    {
        texture = null;
        release = null;
        int index = -1;
        lock (_gate)
        {
            for (int i = 0; i < _busy.Length; i++)
            {
                if (_busy[i])
                    continue;
                _busy[i] = true;
                index = i;
                break;
            }
        }

        if (index < 0)
            return false;

        try
        {
            Draw(index);
        }
        catch
        {
            Return(index);
            throw;
        }

        int captured = index;
        texture = _slots[index].Texture;
        release = () => Return(captured);
        return true;
    }

    void Draw(int index)
    {
        ID3D11DeviceContext context = _device.Context;
        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        context.IASetInputLayout(null);
        context.VSSetShader(_vertexShader);
        context.VSSetConstantBuffers(0, 1, _cropSlot);
        context.PSSetShader(_pixelShader);
        context.PSSetSampler(0, _sampler);
        context.PSSetShaderResource(0, _sourceView);
        ScreenRect destination = _preserveAspect
            ? FrameLayout.Fit(_crop.Width, _crop.Height, _outputWidth, _outputHeight)
            : new ScreenRect(0, 0, _outputWidth, _outputHeight);
        context.RSSetViewport(destination.X, destination.Y, destination.Width, destination.Height);
        context.OMSetRenderTargets(_slots[index].View, null);
        context.ClearRenderTargetView(_slots[index].View, new Vortice.Mathematics.Color4(0, 0, 0, 1));
        context.Draw(3, 0);
        context.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>(), null);
        context.PSSetShaderResource(0, null!);
        context.Flush();
    }

    void Return(int index)
    {
        lock (_gate)
            _busy[index] = false;
    }

    public void Dispose()
    {
        foreach (Slot slot in _slots)
        {
            slot.View.Dispose();
            slot.Texture.Dispose();
        }

        _cropBuffer.Dispose();
        _sampler.Dispose();
        _sourceView.Dispose();
        Source.Dispose();
        _pixelShader.Dispose();
        _vertexShader.Dispose();
    }

    static unsafe void WriteCrop(ID3D11DeviceContext context, ID3D11Buffer buffer, int cropX, int cropY, int cropWidth, int cropHeight, int inputWidth, int inputHeight)
    {
        var data = new CropConstants
        {
            OriginX = cropX / (float)Math.Max(1, inputWidth),
            OriginY = cropY / (float)Math.Max(1, inputHeight),
            ExtentX = cropWidth / (float)Math.Max(1, inputWidth),
            ExtentY = cropHeight / (float)Math.Max(1, inputHeight)
        };
        context.Map(buffer, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None, out MappedSubresource mapped).CheckError();
        *(CropConstants*)mapped.DataPointer = data;
        context.Unmap(buffer, 0);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct CropConstants
    {
        public float OriginX;
        public float OriginY;
        public float ExtentX;
        public float ExtentY;
    }

    sealed class Slot(ID3D11Texture2D texture, ID3D11RenderTargetView view)
    {
        public ID3D11Texture2D Texture { get; } = texture;
        public ID3D11RenderTargetView View { get; } = view;
    }
}

static class ShaderCompiler
{
    public static byte[] Compile(string source, string entry, string profile)
    {
        byte[] text = System.Text.Encoding.ASCII.GetBytes(source);
        nint entryName = Marshal.StringToHGlobalAnsi(entry);
        nint profileName = Marshal.StringToHGlobalAnsi(profile);
        try
        {
            unsafe
            {
                fixed (byte* pointer = text)
                {
                    int hr = D3DCompile((nint)pointer, (nuint)text.Length, 0, 0, 0, entryName, profileName, 1u << 14, 0, out nint code, out nint errors);
                    if (hr < 0)
                    {
                        string message = ReadBlob(errors);
                        if (errors != 0)
                            Marshal.Release(errors);
                        throw new InvalidOperationException(string.IsNullOrWhiteSpace(message) ? Loc.Format("ShaderFailed", entry) : message);
                    }

                    byte[] bytecode = ReadBytes(code);
                    Marshal.Release(code);
                    if (errors != 0)
                        Marshal.Release(errors);
                    return bytecode;
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(entryName);
            Marshal.FreeHGlobal(profileName);
        }
    }

    static byte[] ReadBytes(nint blobPointer)
    {
        if (blobPointer == 0)
            return [];
        var blob = (ID3DBlob)Marshal.GetObjectForIUnknown(blobPointer);
        int size = (int)blob.GetBufferSize();
        var bytes = new byte[size];
        Marshal.Copy(blob.GetBufferPointer(), bytes, 0, size);
        return bytes;
    }

    static string ReadBlob(nint blobPointer)
    {
        if (blobPointer == 0)
            return "";
        var blob = (ID3DBlob)Marshal.GetObjectForIUnknown(blobPointer);
        return Marshal.PtrToStringAnsi(blob.GetBufferPointer(), (int)blob.GetBufferSize()) ?? "";
    }

    [DllImport("d3dcompiler_47.dll", CallingConvention = CallingConvention.StdCall)]
    static extern int D3DCompile(
        nint srcData,
        nuint srcDataSize,
        nint sourceName,
        nint defines,
        nint include,
        nint entrypoint,
        nint target,
        uint flags1,
        uint flags2,
        out nint code,
        out nint errorMsgs);

    [ComImport]
    [Guid("8BA5FB08-5195-40e2-AC58-0D989C3A0102")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ID3DBlob
    {
        [PreserveSig]
        nint GetBufferPointer();

        [PreserveSig]
        nuint GetBufferSize();
    }
}
