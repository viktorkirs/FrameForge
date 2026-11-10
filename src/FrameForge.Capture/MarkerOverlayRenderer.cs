using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FrameForge.Core;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace FrameForge.Capture;

sealed class MarkerOverlayRenderer : IDisposable
{
    const int BatchSize = 64;
    const string Shader = """
        cbuffer MarkerData : register(b1)
        {
            float2 targetSize;
            float2 captureOrigin;
            float2 captureScale;
            float2 padding;
            float4 segments[64];
            float4 widths[64];
        };
        Texture2D markerTexture : register(t0);
        float2 Corner(uint vertex)
        {
            return float2((vertex == 1 || vertex == 4 || vertex == 5) ? 1 : 0,
                          (vertex == 2 || vertex == 3 || vertex == 5) ? 1 : 0);
        }
        float4 Clip(float2 pixelPoint)
        {
            return float4(pixelPoint.x / targetSize.x * 2 - 1, 1 - pixelPoint.y / targetSize.y * 2, 0, 1);
        }
        struct InkVertex
        {
            float4 position : SV_Position;
            nointerpolation float2 start : TEXCOORD0;
            nointerpolation float2 end : TEXCOORD1;
            nointerpolation float radius : TEXCOORD2;
        };
        InkVertex InkVS(uint id : SV_VertexID)
        {
            uint index = id / 6;
            InkVertex output;
            output.start = (segments[index].xy - captureOrigin) * captureScale;
            output.end = (segments[index].zw - captureOrigin) * captureScale;
            output.radius = widths[index].x * max(captureScale.x, captureScale.y) / 2;
            float2 low = min(output.start, output.end) - output.radius - 1;
            float2 high = max(output.start, output.end) + output.radius + 1;
            output.position = Clip(lerp(low, high, Corner(id % 6)));
            return output;
        }
        float4 InkPS(InkVertex input) : SV_Target
        {
            float2 delta = input.end - input.start;
            float along = saturate(dot(input.position.xy - input.start, delta) / max(dot(delta, delta), 0.0001));
            float distance = length(input.position.xy - input.start - along * delta);
            float alpha = 1 - smoothstep(input.radius - 0.7, input.radius + 0.7, distance);
            if (alpha < 0.002) discard;
            return float4(1, 216.0 / 255.0, 51.0 / 255.0, alpha * 0.5);
        }
        float4 CompositeVS(uint id : SV_VertexID) : SV_Position { return Clip(Corner(id) * targetSize); }
        float4 CompositePS(float4 position : SV_Position) : SV_Target { return markerTexture.Load(int3(position.xy, 0)); }
        """;

    readonly GraphicsDevice _device;
    readonly int _width, _height;
    readonly ID3D11Texture2D _texture;
    readonly ID3D11RenderTargetView _view;
    readonly ID3D11ShaderResourceView _resource;
    readonly ID3D11VertexShader _inkVs, _compositeVs;
    readonly ID3D11PixelShader _inkPs, _compositePs;
    readonly ID3D11Buffer _constants;
    readonly ID3D11Buffer[] _slot;
    readonly ID3D11BlendState _maximum, _alpha;
    long _version = -1;
    (float X, float Y, float ScaleX, float ScaleY) _mapping;

    public MarkerOverlayRenderer(GraphicsDevice device, int width, int height)
    {
        _device = device; _width = width; _height = height;
        _texture = device.Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default, BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource
        });
        _view = device.Device.CreateRenderTargetView(_texture);
        _resource = device.Device.CreateShaderResourceView(_texture);
        _inkVs = device.Device.CreateVertexShader(ShaderCompiler.Compile(Shader, "InkVS", "vs_5_0"));
        _inkPs = device.Device.CreatePixelShader(ShaderCompiler.Compile(Shader, "InkPS", "ps_5_0"));
        _compositeVs = device.Device.CreateVertexShader(ShaderCompiler.Compile(Shader, "CompositeVS", "vs_5_0"));
        _compositePs = device.Device.CreatePixelShader(ShaderCompiler.Compile(Shader, "CompositePS", "ps_5_0"));
        _constants = device.Device.CreateBuffer(new BufferDescription
        {
            ByteWidth = (uint)Unsafe.SizeOf<Constants>(), Usage = ResourceUsage.Dynamic,
            BindFlags = BindFlags.ConstantBuffer, CPUAccessFlags = CpuAccessFlags.Write
        });
        _slot = [_constants];
        BlendDescription maximum = BlendDescription.Opaque;
        maximum.RenderTarget[0] = new RenderTargetBlendDescription
        {
            BlendEnable = true, SourceBlend = Blend.One, DestinationBlend = Blend.One, BlendOperation = BlendOperation.Max,
            SourceBlendAlpha = Blend.One, DestinationBlendAlpha = Blend.One, BlendOperationAlpha = BlendOperation.Max,
            RenderTargetWriteMask = ColorWriteEnable.All
        };
        _maximum = device.Device.CreateBlendState(maximum);
        _alpha = device.Device.CreateBlendState(BlendDescription.NonPremultiplied);
    }

    public unsafe void Draw(ID3D11RenderTargetView destination, MarkerSnapshot snapshot, (float X, float Y, float ScaleX, float ScaleY) mapping)
    {
        if (snapshot.Segments.Length == 0) return;
        ID3D11DeviceContext context = _device.Context;
        context.VSSetConstantBuffers(1, 1, _slot);
        context.PSSetConstantBuffers(1, 1, _slot);
        if (_version != snapshot.Version || _mapping != mapping)
        {
            context.PSSetShaderResource(0, null!);
            context.OMSetRenderTargets(_view, null);
            context.ClearRenderTargetView(_view, new Color4(0, 0, 0, 0));
            context.OMSetBlendState(_maximum, null, uint.MaxValue);
            context.VSSetShader(_inkVs);
            context.PSSetShader(_inkPs);
            for (int offset = 0; offset < snapshot.Segments.Length; offset += BatchSize)
            {
                Constants data = default;
                data.Width = _width; data.Height = _height;
                data.OriginX = mapping.X; data.OriginY = mapping.Y;
                data.ScaleX = mapping.ScaleX; data.ScaleY = mapping.ScaleY;
                int count = Math.Min(BatchSize, snapshot.Segments.Length - offset);
                for (int i = 0; i < count; i++)
                {
                    MarkerSegment segment = snapshot.Segments[offset + i];
                    data.Segments[i * 4] = segment.Start.X;
                    data.Segments[i * 4 + 1] = segment.Start.Y;
                    data.Segments[i * 4 + 2] = segment.End.X;
                    data.Segments[i * 4 + 3] = segment.End.Y;
                    data.Widths[i * 4] = segment.Width;
                }
                context.Map(_constants, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None, out MappedSubresource mapped).CheckError();
                *(Constants*)mapped.DataPointer = data;
                context.Unmap(_constants, 0);
                context.Draw((uint)(count * 6), 0);
            }
            _version = snapshot.Version;
            _mapping = mapping;
        }
        context.OMSetRenderTargets(destination, null);
        context.OMSetBlendState(_alpha, null, uint.MaxValue);
        context.VSSetShader(_compositeVs);
        context.PSSetShader(_compositePs);
        context.PSSetShaderResource(0, _resource);
        context.Draw(6, 0);
        context.PSSetShaderResource(0, null!);
    }

    public void Dispose()
    {
        _maximum.Dispose(); _alpha.Dispose(); _constants.Dispose();
        _inkVs.Dispose(); _inkPs.Dispose(); _compositeVs.Dispose(); _compositePs.Dispose();
        _resource.Dispose(); _view.Dispose(); _texture.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    unsafe struct Constants
    {
        public float Width, Height, OriginX, OriginY, ScaleX, ScaleY, Pad0, Pad1;
        public fixed float Segments[BatchSize * 4];
        public fixed float Widths[BatchSize * 4];
    }
}
