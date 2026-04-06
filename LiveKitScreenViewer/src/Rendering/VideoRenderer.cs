using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Text;
using System.Numerics;
using LiveKitScreenViewer.Frames;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace LiveKitScreenViewer.Rendering;

public sealed class VideoRenderer : IDisposable
{
    private readonly DxDeviceManager _deviceManager;
    private readonly SwapChainPanelHost _panelHost;
    private readonly ID3D11VertexShader _vertexShader;
    private readonly ID3D11PixelShader _pixelShader;
    private readonly ID3D11InputLayout _inputLayout;
    private readonly ID3D11Buffer _vertexBuffer;
    private readonly ID3D11Buffer _transformBuffer;
    private readonly ID3D11SamplerState _samplerState;
    private readonly uint _vertexStride = (uint)Marshal.SizeOf<QuadVertex>();
    private ID3D11Texture2D? _frameTexture;
    private ID3D11ShaderResourceView? _frameShaderResourceView;
    private uint _frameWidth;
    private uint _frameHeight;
    private double _currentFramesPerSecond;
    private int _framesPresentedSinceLastSample;
    private DateTime _lastFpsSampleUtc = DateTime.UtcNow;

    public VideoRenderer(DxDeviceManager deviceManager, SwapChainPanelHost panelHost)
    {
        _deviceManager = deviceManager;
        _panelHost = panelHost;

        var vertexShaderBytes = CompileShader(VertexShaderSource, "VSMain", "vs_4_0");
        var pixelShaderBytes = CompileShader(PixelShaderSource, "PSMain", "ps_4_0");

        _vertexShader = _deviceManager.Device.CreateVertexShader(vertexShaderBytes, null);
        _pixelShader = _deviceManager.Device.CreatePixelShader(pixelShaderBytes, null);
        _inputLayout = _deviceManager.Device.CreateInputLayout(
            [
                new InputElementDescription("POSITION", 0, Format.R32G32_Float, 0, 0),
                new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float, 8, 0),
            ],
            vertexShaderBytes);

        var quadVertices = new[]
        {
            new QuadVertex(-1f, -1f, 0f, 1f),
            new QuadVertex(-1f,  1f, 0f, 0f),
            new QuadVertex( 1f, -1f, 1f, 1f),
            new QuadVertex( 1f,  1f, 1f, 0f),
        };

        var vertexBufferDescription = new BufferDescription(
            _vertexStride * (uint)quadVertices.Length,
            BindFlags.VertexBuffer,
            ResourceUsage.Immutable,
            CpuAccessFlags.None,
            ResourceOptionFlags.None,
            0);

        unsafe
        {
            fixed (QuadVertex* vertices = quadVertices)
            {
                var vertexSubresource = new SubresourceData((IntPtr)vertices, 0, 0);
                _deviceManager.Device.CreateBuffer(vertexBufferDescription, vertexSubresource, out _vertexBuffer).CheckError();
            }
        }

        _samplerState = _deviceManager.Device.CreateSamplerState(new SamplerDescription(
            Filter.MinMagMipLinear,
            TextureAddressMode.Clamp,
            TextureAddressMode.Clamp,
            TextureAddressMode.Clamp,
            0.0f,
            1,
            ComparisonFunction.Never,
            new Color4(0f, 0f, 0f, 0f),
            0.0f,
            float.MaxValue));

        _transformBuffer = _deviceManager.Device.CreateBuffer(new BufferDescription(
            (uint)Marshal.SizeOf<TransformConstants>(),
            BindFlags.ConstantBuffer,
            ResourceUsage.Dynamic,
            CpuAccessFlags.Write,
            ResourceOptionFlags.None,
            0));
    }

    public double CurrentFramesPerSecond => _currentFramesPerSecond;

    public void Render(VideoFrame? frame)
    {
        if (frame is not null)
        {
            UploadFrame(frame);
        }

        if (_frameTexture is null || _frameShaderResourceView is null)
        {
            return;
        }

        _panelHost.EnsureSwapChain(_frameWidth, _frameHeight);

        var context = _deviceManager.Context;
        context.ClearRenderTargetView(_panelHost.RenderTargetView, new Color4(0.0f, 0.0f, 0.0f, 1.0f));
        context.RSSetViewports(new[] { CreateFullViewport() });
        context.OMSetRenderTargets(new[] { _panelHost.RenderTargetView }, null);
        context.PSSetShaderResources(0, new[] { _frameShaderResourceView });
        context.PSSetSamplers(0, new[] { _samplerState });
        context.IASetVertexBuffers(0, new[] { _vertexBuffer }, new uint[] { _vertexStride }, new uint[] { 0 });
        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
        context.IASetInputLayout(_inputLayout);
        UpdateAspectFitTransform();
        context.VSSetConstantBuffers(0, new[] { _transformBuffer });
        context.VSSetShader(_vertexShader);
        context.PSSetShader(_pixelShader);
        context.Draw(4, 0);

        _panelHost.Present();
        UpdatePresentedFramesPerSecond();
    }

    public void Dispose()
    {
        _frameShaderResourceView?.Dispose();
        _frameTexture?.Dispose();
        _transformBuffer.Dispose();
        _samplerState.Dispose();
        _vertexBuffer.Dispose();
        _inputLayout.Dispose();
        _pixelShader.Dispose();
        _vertexShader.Dispose();
    }

    private void UploadFrame(VideoFrame frame)
    {
        var width = (uint)frame.Width;
        var height = (uint)frame.Height;

        if (_frameTexture is null || _frameWidth != width || _frameHeight != height)
        {
            _frameShaderResourceView?.Dispose();
            _frameTexture?.Dispose();

            _frameTexture = _deviceManager.Device.CreateTexture2D(new Texture2DDescription
            {
                Width = width,
                Height = height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.R8G8B8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource,
            });

            _frameShaderResourceView = _deviceManager.Device.CreateShaderResourceView(_frameTexture, null);
            _frameWidth = width;
            _frameHeight = height;
        }

        unsafe
        {
            fixed (byte* pixelData = frame.Data)
            {
                _deviceManager.Context.UpdateSubresource(_frameTexture!, 0, null, new IntPtr(pixelData), (uint)frame.Stride, 0);
            }
        }
    }

    private Viewport CreateFullViewport()
    {
        return new Viewport(0, 0, _panelHost.BufferWidth, _panelHost.BufferHeight);
    }

    private void UpdateAspectFitTransform()
    {
        if (_frameWidth == 0 || _frameHeight == 0 || _panelHost.BufferWidth == 0 || _panelHost.BufferHeight == 0)
        {
            return;
        }

        var frameAspectRatio = (float)_frameWidth / _frameHeight;
        var panelAspectRatio = (float)_panelHost.BufferWidth / _panelHost.BufferHeight;
        var scale = Vector2.One;

        if (panelAspectRatio > frameAspectRatio)
        {
            scale.X = frameAspectRatio / panelAspectRatio;
        }
        else
        {
            scale.Y = panelAspectRatio / frameAspectRatio;
        }

        var mappedResource = _deviceManager.Context.Map(_transformBuffer, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        try
        {
            unsafe
            {
                *(TransformConstants*)mappedResource.DataPointer = new TransformConstants(scale.X, scale.Y);
            }
        }
        finally
        {
            _deviceManager.Context.Unmap(_transformBuffer, 0);
        }
    }

    private void UpdatePresentedFramesPerSecond()
    {
        _framesPresentedSinceLastSample++;

        var now = DateTime.UtcNow;
        var elapsed = now - _lastFpsSampleUtc;
        if (elapsed.TotalMilliseconds < 250)
        {
            return;
        }

        _currentFramesPerSecond = _framesPresentedSinceLastSample / elapsed.TotalSeconds;
        _framesPresentedSinceLastSample = 0;
        _lastFpsSampleUtc = now;
    }

    private static byte[] CompileShader(string source, string entryPoint, string target)
    {
        var sourceBytes = Encoding.ASCII.GetBytes(source);

        unsafe
        {
            fixed (byte* sourcePointer = sourceBytes)
            {
                var hr = D3DCompile(
                    sourcePointer,
                    (nuint)sourceBytes.Length,
                    null,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    entryPoint,
                    target,
                    D3DCOMPILE_OPTIMIZATION_LEVEL3,
                    0,
                    out var shaderBlobPointer,
                    out var errorBlobPointer);

                try
                {
                    if (hr < 0)
                    {
                        var message = errorBlobPointer != IntPtr.Zero
                            ? ReadBlobAsAnsi(errorBlobPointer)
                            : $"D3DCompile failed with HRESULT 0x{hr:X8}.";
                        throw new InvalidOperationException(message);
                    }

                    if (shaderBlobPointer == IntPtr.Zero)
                    {
                        throw new InvalidOperationException("D3DCompile succeeded but did not return shader bytecode.");
                    }

                    return CopyBlobBytes(shaderBlobPointer);
                }
                finally
                {
                    if (shaderBlobPointer != IntPtr.Zero)
                    {
                        Marshal.Release(shaderBlobPointer);
                    }

                    if (errorBlobPointer != IntPtr.Zero)
                    {
                        Marshal.Release(errorBlobPointer);
                    }
                }
            }
        }
    }

    private static byte[] CopyBlobBytes(IntPtr blobPointer)
    {
        unsafe
        {
            var blob = (ID3DBlob*)blobPointer;
            var size = checked((int)blob->GetBufferSize());
            var bytes = new byte[size];
            Marshal.Copy(blob->GetBufferPointer(), bytes, 0, size);
            return bytes;
        }
    }

    private static string ReadBlobAsAnsi(IntPtr blobPointer)
    {
        unsafe
        {
            var blob = (ID3DBlob*)blobPointer;
            var size = checked((int)blob->GetBufferSize());
            return Marshal.PtrToStringAnsi(blob->GetBufferPointer(), size)?.TrimEnd('\0', '\r', '\n')
                ?? "Unknown shader compiler error.";
        }
    }

    private const uint D3DCOMPILE_OPTIMIZATION_LEVEL3 = 1u << 15;

    [DllImport("d3dcompiler_47.dll", EntryPoint = "D3DCompile", CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    private static extern unsafe int D3DCompile(
        void* srcData,
        nuint srcDataSize,
        [MarshalAs(UnmanagedType.LPStr)] string? sourceName,
        IntPtr defines,
        IntPtr include,
        [MarshalAs(UnmanagedType.LPStr)] string entryPoint,
        [MarshalAs(UnmanagedType.LPStr)] string target,
        uint flags1,
        uint flags2,
        out IntPtr code,
        out IntPtr errorMessages);

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct ID3DBlob
    {
        public void** VTable;

        public IntPtr GetBufferPointer()
        {
            var method = (delegate* unmanaged[Stdcall]<ID3DBlob*, IntPtr>)VTable[3];
            return method((ID3DBlob*)Unsafe.AsPointer(ref this));
        }

        public nuint GetBufferSize()
        {
            var method = (delegate* unmanaged[Stdcall]<ID3DBlob*, nuint>)VTable[4];
            return method((ID3DBlob*)Unsafe.AsPointer(ref this));
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct QuadVertex
    {
        public QuadVertex(float x, float y, float u, float v)
        {
            X = x;
            Y = y;
            U = u;
            V = v;
        }

        public float X { get; }

        public float Y { get; }

        public float U { get; }

        public float V { get; }
    }

    private const string VertexShaderSource = """
struct VSInput
{
    float2 position : POSITION;
    float2 texCoord : TEXCOORD0;
};

cbuffer TransformConstants : register(b0)
{
    float2 videoScale;
    float2 padding;
};

struct PSInput
{
    float4 position : SV_POSITION;
    float2 texCoord : TEXCOORD0;
};

PSInput VSMain(VSInput input)
{
    PSInput output;
    output.position = float4(input.position.xy * videoScale, 0.0f, 1.0f);
    output.texCoord = input.texCoord;
    return output;
}
""";

    private const string PixelShaderSource = """
Texture2D sourceTexture : register(t0);
SamplerState sourceSampler : register(s0);

float4 PSMain(float4 position : SV_POSITION, float2 texCoord : TEXCOORD0) : SV_TARGET
{
    return sourceTexture.Sample(sourceSampler, texCoord);
}
""";

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct TransformConstants
    {
        public TransformConstants(float scaleX, float scaleY)
        {
            ScaleX = scaleX;
            ScaleY = scaleY;
            Padding0 = 0f;
            Padding1 = 0f;
        }

        public float ScaleX { get; }

        public float ScaleY { get; }

        public float Padding0 { get; }

        public float Padding1 { get; }
    }
}
