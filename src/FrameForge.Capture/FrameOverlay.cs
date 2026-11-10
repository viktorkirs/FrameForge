using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FrameForge.Core;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace FrameForge.Capture;

/// <summary>
/// Copies a captured BGRA frame and draws click rings and a smooth cursor into it
/// before that texture is scaled and encoded.
/// </summary>
sealed class FrameOverlay : IDisposable
{
    const string ShaderSource = """
        cbuffer Draw : register(b0)
        {
            float2 origin;
            float2 quadSize;
            float2 targetSize;
            float2 center;
            float age;
            float mode;
            float2 pad;
        };

        struct Vertex
        {
            float4 position : SV_Position;
        };

        Vertex VS(uint id : SV_VertexID)
        {
            float x = (id == 1 || id == 4 || id == 5) ? 1.0 : 0.0;
            float y = (id == 2 || id == 3 || id == 5) ? 1.0 : 0.0;
            float2 pixel = origin + float2(x, y) * quadSize;
            float2 ndc = float2((pixel.x / targetSize.x) * 2.0 - 1.0, 1.0 - (pixel.y / targetSize.y) * 2.0);
            Vertex output;
            output.position = float4(ndc, 0.0, 1.0);
            return output;
        }

        bool InsideTriangle(float2 p, float2 a, float2 b, float2 c)
        {
            float2 v0 = c - a;
            float2 v1 = b - a;
            float2 v2 = p - a;
            float d00 = dot(v0, v0);
            float d01 = dot(v0, v1);
            float d02 = dot(v0, v2);
            float d11 = dot(v1, v1);
            float d12 = dot(v1, v2);
            float denom = d00 * d11 - d01 * d01;
            float u = (d11 * d02 - d01 * d12) / denom;
            float v = (d00 * d12 - d01 * d02) / denom;
            return u >= -0.001 && v >= -0.001 && (u + v) <= 1.001;
        }

        bool Arrow(float2 p)
        {
            float2 p0 = float2(0.0, 0.0);
            float2 p1 = float2(0.0, 16.5);
            float2 p2 = float2(4.4, 12.4);
            float2 p3 = float2(7.2, 19.6);
            float2 p4 = float2(9.8, 18.3);
            float2 p5 = float2(6.8, 11.5);
            float2 p6 = float2(12.6, 11.2);
            return InsideTriangle(p, p0, p1, p2)
                || InsideTriangle(p, p0, p2, p5)
                || InsideTriangle(p, p0, p5, p6)
                || InsideTriangle(p, p2, p3, p4)
                || InsideTriangle(p, p2, p4, p5);
        }

        float4 Ring(float2 pixel)
        {
            float dist = length(pixel - center);
            float radius = 18.0 + age * 8.0;
            float delta = abs(dist - radius);
            float core = 1.0 - smoothstep(1.15, 2.15, delta);
            float halo = saturate(1.0 - smoothstep(1.9, 3.3, delta)) * (1.0 - core);
            float fade = 1.0 - smoothstep(0.0, 1.0, age);
            float alpha = (core * 0.88 + halo * 0.70) * fade;
            if (alpha < 0.02)
                discard;
            float3 rgb = lerp(float3(0.06, 0.07, 0.09), float3(0.93, 0.96, 1.0), saturate(core * 1.35));
            return float4(rgb, alpha);
        }

        float4 Cursor(float2 pixel)
        {
            float2 local = pixel - center;
            bool fill = Arrow(local);
            bool outline = false;
            if (!fill)
            {
                outline = Arrow(local + float2(1.15, 0.0))
                    || Arrow(local + float2(-1.15, 0.0))
                    || Arrow(local + float2(0.0, 1.15))
                    || Arrow(local + float2(0.0, -1.15))
                    || Arrow(local + float2(0.9, 0.9))
                    || Arrow(local + float2(-0.9, 0.9))
                    || Arrow(local + float2(0.9, -0.9))
                    || Arrow(local + float2(-0.9, -0.9));
            }

            if (fill)
                return float4(1.0, 1.0, 1.0, 0.96);
            if (outline)
                return float4(0.05, 0.06, 0.08, 0.94);
            discard;
            return float4(0.0, 0.0, 0.0, 0.0);
        }

        float4 PS(float4 position : SV_Position) : SV_Target
        {
            if (mode < 0.5)
                return Ring(position.xy);
            return Cursor(position.xy);
        }
        """;

    const float RingReach = 36f;

    readonly GraphicsDevice _device;
    readonly ID3D11Texture2D _color;
    readonly ID3D11RenderTargetView _view;
    readonly ID3D11VertexShader _vertexShader;
    readonly ID3D11PixelShader _pixelShader;
    readonly ID3D11Buffer _constants;
    readonly ID3D11BlendState _blend;
    readonly ID3D11RasterizerState _raster;
    readonly int _width;
    readonly int _height;
    readonly ClickGlyph[] _clicks = new ClickGlyph[8];
    readonly MarkerOverlayRenderer? _markers;

    public FrameOverlay(GraphicsDevice device, int width, int height, bool markers = false)
    {
        _device = device;
        _width = width;
        _height = height;
        _color = device.Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None
        });
        _view = device.Device.CreateRenderTargetView(_color);
        _vertexShader = device.Device.CreateVertexShader(ShaderCompiler.Compile(ShaderSource, "VS", "vs_5_0"));
        _pixelShader = device.Device.CreatePixelShader(ShaderCompiler.Compile(ShaderSource, "PS", "ps_5_0"));
        _constants = device.Device.CreateBuffer(new BufferDescription
        {
            ByteWidth = (uint)Unsafe.SizeOf<DrawConstants>(),
            Usage = ResourceUsage.Dynamic,
            BindFlags = BindFlags.ConstantBuffer,
            CPUAccessFlags = CpuAccessFlags.Write,
            MiscFlags = ResourceOptionFlags.None
        });
        _blend = device.Device.CreateBlendState(BlendDescription.NonPremultiplied);
        _raster = device.Device.CreateRasterizerState(RasterizerDescription.CullNone);
        _constantSlot = [_constants];
        if (markers) _markers = new MarkerOverlayRenderer(device, width, height);
    }

    public ID3D11Texture2D Texture => _color;
    public bool Ready { get; private set; }

    public void Compose(ID3D11Texture2D source, PointerTrack pointer, MarkerSnapshot? markers = null)
    {
        ID3D11DeviceContext context = _device.Context;
        context.CopyResource(_color, source);
        Ready = true;

        int clicks = pointer.CopyClicks(_clicks);
        bool cursor = pointer.DrawCursor;
        bool ink = _markers != null && markers is { Segments.Length: > 0 };
        if (clicks == 0 && !cursor && !ink)
            return;

        bool bound = false;
        try
        {
            context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            context.IASetInputLayout(null);
            context.VSSetShader(_vertexShader);
            context.PSSetShader(_pixelShader);
            context.RSSetState(_raster);
            context.RSSetViewport(0, 0, _width, _height);
            SetBlend(context, _blend);
            context.OMSetDepthStencilState(null, 0);
            context.OMSetRenderTargets(_view, null);
            BindConstants(context);
            bound = true;

            if (ink)
            {
                _markers!.Draw(_view, markers!, pointer.Mapping);
                context.VSSetShader(_vertexShader);
                context.PSSetShader(_pixelShader);
                BindConstants(context);
                SetBlend(context, _blend);
            }

            for (int i = 0; i < clicks; i++)
                Draw(_clicks[i].X, _clicks[i].Y, RingReach, _clicks[i].Age, mode: 0);
            if (cursor)
                Draw(pointer.CursorX - 4f, pointer.CursorY - 4f, 0f, 0f, mode: 1, cursor: true, hotspotX: pointer.CursorX, hotspotY: pointer.CursorY);
        }
        finally
        {
            if (bound)
            {
                context.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>(), null);
                SetBlend(context, null);
                context.RSSetState(null);
            }
        }
    }

    void Draw(float originX, float originY, float reach, float age, float mode, bool cursor = false, float hotspotX = 0, float hotspotY = 0)
    {
        DrawConstants data = cursor
            ? new DrawConstants
            {
                OriginX = originX,
                OriginY = originY,
                SizeX = 22f,
                SizeY = 28f,
                TargetWidth = _width,
                TargetHeight = _height,
                CenterX = hotspotX,
                CenterY = hotspotY,
                Age = 0,
                Mode = 1
            }
            : new DrawConstants
            {
                OriginX = originX - reach,
                OriginY = originY - reach,
                SizeX = reach * 2f,
                SizeY = reach * 2f,
                TargetWidth = _width,
                TargetHeight = _height,
                CenterX = originX,
                CenterY = originY,
                Age = age,
                Mode = mode
            };

        ID3D11DeviceContext context = _device.Context;
        context.Map(_constants, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None, out MappedSubresource mapped).CheckError();
        unsafe
        {
            *(DrawConstants*)mapped.DataPointer = data;
        }

        context.Unmap(_constants, 0);
        context.Draw(6, 0);
    }

    readonly ID3D11Buffer[] _constantSlot;

    void BindConstants(ID3D11DeviceContext context)
    {
        context.VSSetConstantBuffers(0, 1, _constantSlot);
        context.PSSetConstantBuffers(0, 1, _constantSlot);
    }

    static unsafe void SetBlend(ID3D11DeviceContext context, ID3D11BlendState? state) =>
        context.OMSetBlendState(state, null, 0xFFFFFFFF);

    public void Dispose()
    {
        _markers?.Dispose();
        _raster.Dispose();
        _blend.Dispose();
        _constants.Dispose();
        _pixelShader.Dispose();
        _vertexShader.Dispose();
        _view.Dispose();
        _color.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    struct DrawConstants
    {
        public float OriginX;
        public float OriginY;
        public float SizeX;
        public float SizeY;
        public float TargetWidth;
        public float TargetHeight;
        public float CenterX;
        public float CenterY;
        public float Age;
        public float Mode;
        public float Pad0;
        public float Pad1;
    }
}

struct ClickGlyph
{
    public float X;
    public float Y;
    public float Age;
}

/// <summary>
/// Samples the real cursor and animates its recorded image with continuous inertia.
/// </summary>
sealed class PointerTrack
{
    int _originX;
    int _originY;
    float _scaleX;
    float _scaleY;
    readonly int _captureWidth;
    readonly int _captureHeight;
    readonly nint _window;
    readonly bool _smooth;
    readonly bool _highlight;
    readonly (float X, float Y, long At)[] _clicks = new (float X, float Y, long At)[8];
    int _clickCount;
    long _lastWall;
    long _media;
    bool _clockReady;
    bool _leftDown;
    bool _rightDown;
    bool _hasCursor;
    readonly SmoothCursorMotion _motion;
    long _motionAt;
    long _preciseUntil;
    float _drawnX;
    float _drawnY;

    public PointerTrack(MonitorInfo monitor, int captureWidth, int captureHeight, bool smooth, bool highlight, nint window = 0,
        CursorMotionStyle motion = CursorMotionStyle.Curve, int smoothnessMilliseconds = 700)
    {
        _originX = monitor.Left;
        _originY = monitor.Top;
        _captureWidth = captureWidth;
        _captureHeight = captureHeight;
        _window = window;
        _scaleX = captureWidth / (float)Math.Max(1, monitor.Width);
        _scaleY = captureHeight / (float)Math.Max(1, monitor.Height);
        _smooth = smooth;
        _highlight = highlight;
        _motion = new SmoothCursorMotion(motion, smoothnessMilliseconds);
    }

    public bool DrawCursor => _smooth && _hasCursor;
    public float CursorX => _drawnX;
    public float CursorY => _drawnY;
    public (float X, float Y, float ScaleX, float ScaleY) Mapping => (_originX, _originY, _scaleX, _scaleY);

    public void FreezeClock(long wall)
    {
        _lastWall = wall;
        if (!_clockReady)
            return;

        _leftDown = (GetAsyncKeyState(0x01) & 0x8000) != 0;
        _rightDown = (GetAsyncKeyState(0x02) & 0x8000) != 0;
    }

    public void Sample(long wall)
    {
        if (!_clockReady)
        {
            _clockReady = true;
            _lastWall = wall;
            _media = 0;
        }
        else
        {
            long delta = wall - _lastWall;
            if (delta < 0)
                delta = 0;
            _media += delta;
            _lastWall = wall;
        }

        if (!TryMapCursor(out float x, out float y))
            return;

        bool left = (GetAsyncKeyState(0x01) & 0x8000) != 0;
        bool right = (GetAsyncKeyState(0x02) & 0x8000) != 0;
        bool click = (left && !_leftDown) || (right && !_rightDown);
        _leftDown = left;
        _rightDown = right;

        if (_highlight && click)
            PushClick(x, y, _media);

        if (!_smooth)
            return;

        if (!_hasCursor)
        {
            _hasCursor = true;
            _motion.Reset(x, y);
            _drawnX = x;
            _drawnY = y;
            _motionAt = _media;
            return;
        }

        if (click)
            _preciseUntil = _media + Qpc.Frequency / 4;
        _motion.Advance(x, y, (_media - _motionAt) / (double)Qpc.Frequency, left || right || _media < _preciseUntil);
        _motionAt = _media;
        _drawnX = _motion.Position.X;
        _drawnY = _motion.Position.Y;
    }

    public int CopyClicks(ClickGlyph[] destination)
    {
        if (!_highlight || _clickCount == 0)
            return 0;

        long life = Qpc.Frequency * 2 / 5;
        int kept = 0;
        for (int i = 0; i < _clickCount; i++)
        {
            long age = _media - _clicks[i].At;
            if (age < 0 || age > life)
                continue;
            _clicks[kept++] = _clicks[i];
        }

        _clickCount = kept;
        int count = Math.Min(kept, destination.Length);
        for (int i = 0; i < count; i++)
        {
            long age = _media - _clicks[i].At;
            destination[i] = new ClickGlyph
            {
                X = _clicks[i].X,
                Y = _clicks[i].Y,
                Age = age / (float)life
            };
        }

        return count;
    }

    void PushClick(float x, float y, long at)
    {
        if (_clickCount == _clicks.Length)
        {
            Array.Copy(_clicks, 1, _clicks, 0, _clicks.Length - 1);
            _clickCount--;
        }

        _clicks[_clickCount++] = (x, y, at);
    }

    bool TryMapCursor(out float x, out float y)
    {
        x = 0;
        y = 0;
        if (_window != 0 && GetWindowRect(_window, out WindowRect bounds))
        {
            int width = Math.Max(1, bounds.Right - bounds.Left);
            int height = Math.Max(1, bounds.Bottom - bounds.Top);
            _originX = bounds.Left;
            _originY = bounds.Top;
            _scaleX = _captureWidth / (float)width;
            _scaleY = _captureHeight / (float)height;
        }

        if (!GetCursorPos(out CursorPoint point))
            return false;

        x = (point.X - _originX) * _scaleX;
        y = (point.Y - _originY) * _scaleY;
        return true;
    }

    [DllImport("user32.dll")]
    static extern bool GetCursorPos(out CursorPoint point);

    [DllImport("user32.dll")]
    static extern bool GetWindowRect(nint hwnd, out WindowRect rect);

    [DllImport("user32.dll")]
    static extern short GetAsyncKeyState(int virtualKey);

    [StructLayout(LayoutKind.Sequential)]
    struct CursorPoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct WindowRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
