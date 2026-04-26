using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI.Xaml.Controls;

namespace ClassControl.Business.VideoCapture;

public sealed class D3D11VideoRenderer : IDisposable
{
    private const uint WarpMaxWidth = 1920;
    private const uint WarpMaxHeight = 1080;

    private readonly DxDeviceManager _deviceManager;
    private readonly SwapChainPanelHost _panelHost;
    private readonly AutoResetEvent _frameSignal = new(false);
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly object _frameLock = new();
    private readonly uint _vertexStride = (uint)Marshal.SizeOf<QuadVertex>();

    private readonly IntPtr _vertexShader;
    private readonly IntPtr _pixelShader;
    private readonly IntPtr _inputLayout;
    private readonly IntPtr _vertexBuffer;
    private readonly IntPtr _samplerState;

    private Thread? _renderThread;
    private D3D11VideoFrame? _latestFrame;
    private IntPtr _frameTexture;
    private IntPtr _frameShaderResourceView;
    private uint _frameWidth;
    private uint _frameHeight;
    private IntPtr _scaledUploadBuffer;
    private int _scaledUploadCapacity;
    private long _nextFrameIndex;
    private long _lastUploadedFrameIndex = -1;
    private bool _disposed;

    public D3D11VideoRenderer(SwapChainPanel panel)
    {
        _deviceManager = new DxDeviceManager();
        _panelHost = new SwapChainPanelHost(panel, _deviceManager);

        var vertexShaderBytes = CompileShader(VertexShaderSource, "VSMain", "vs_4_0");
        var pixelShaderBytes = CompileShader(PixelShaderSource, "PSMain", "ps_4_0");

        _vertexShader = LiveKitD3D11Interop.CreateVertexShader(_deviceManager.Device, vertexShaderBytes);
        _pixelShader = LiveKitD3D11Interop.CreatePixelShader(_deviceManager.Device, pixelShaderBytes);

        unsafe
        {
            ReadOnlySpan<byte> positionSemantic = "POSITION\0"u8;
            ReadOnlySpan<byte> texCoordSemantic = "TEXCOORD\0"u8;

            fixed (byte* pPosition = positionSemantic)
            fixed (byte* pTexCoord = texCoordSemantic)
            {
                var inputElements = new[]
                {
                    new InputElementDescription { SemanticName = (sbyte*)pPosition, SemanticIndex = 0, Format = DxgiFormat.R32G32Float, InputSlot = 0, AlignedByteOffset = 0 },
                    new InputElementDescription { SemanticName = (sbyte*)pTexCoord, SemanticIndex = 0, Format = DxgiFormat.R32G32Float, InputSlot = 0, AlignedByteOffset = 8 },
                };

                _inputLayout = LiveKitD3D11Interop.CreateInputLayout(_deviceManager.Device, inputElements, vertexShaderBytes);
            }
        }

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
                _vertexBuffer = LiveKitD3D11Interop.CreateBuffer(_deviceManager.Device, vertexBufferDescription, &vertexSubresource);
            }
        }

        _samplerState = LiveKitD3D11Interop.CreateSamplerState(
            _deviceManager.Device,
            new SamplerDescription
            {
                Filter = Filter.MinMagMipLinear,
                AddressU = TextureAddressMode.Clamp,
                AddressV = TextureAddressMode.Clamp,
                AddressW = TextureAddressMode.Clamp,
                MipLodBias = 0.0f,
                MaxAnisotropy = 1,
                ComparisonFunc = ComparisonFunction.Never,
                BorderColorR = 0f,
                BorderColorG = 0f,
                BorderColorB = 0f,
                BorderColorA = 0f,
                MinLod = 0.0f,
                MaxLod = float.MaxValue,
            });

        BindPipeline();
        StartRenderThread();
    }

    public void SubmitFrame(byte[] frameData, int width, int height, int stride)
    {
        if (_disposed || frameData.Length == 0 || width <= 0 || height <= 0 || stride <= 0)
        {
            return;
        }

        var byteLength = checked(stride * height);
        if (frameData.Length < byteLength)
        {
            return;
        }

        var copy = new byte[byteLength];
        Buffer.BlockCopy(frameData, 0, copy, 0, byteLength);

        var frame = new D3D11VideoFrame(
            copy,
            width,
            height,
            stride,
            Interlocked.Increment(ref _nextFrameIndex));

        lock (_frameLock)
        {
            _latestFrame = frame;
        }

        _frameSignal.Set();
    }

    public void InvalidatePanelMetrics()
    {
        _panelHost.InvalidatePanelMetrics();
        _frameSignal.Set();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _disposeCts.Cancel();
        _frameSignal.Set();

        _renderThread?.Join();
        _frameSignal.Dispose();
        _disposeCts.Dispose();

        LiveKitD3D11Interop.Release(ref _frameShaderResourceView);
        LiveKitD3D11Interop.Release(ref _frameTexture);

        if (_scaledUploadBuffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_scaledUploadBuffer);
            _scaledUploadBuffer = IntPtr.Zero;
            _scaledUploadCapacity = 0;
        }

        var samplerState = _samplerState;
        LiveKitD3D11Interop.Release(ref samplerState);

        var vertexBuffer = _vertexBuffer;
        LiveKitD3D11Interop.Release(ref vertexBuffer);

        var inputLayout = _inputLayout;
        LiveKitD3D11Interop.Release(ref inputLayout);

        var pixelShader = _pixelShader;
        LiveKitD3D11Interop.Release(ref pixelShader);

        var vertexShader = _vertexShader;
        LiveKitD3D11Interop.Release(ref vertexShader);

        _panelHost.Dispose();
        _deviceManager.Dispose();
    }

    private void StartRenderThread()
    {
        _renderThread = new Thread(RenderLoop)
        {
            IsBackground = true,
            Name = "ClassControl.D3D11VideoRenderer",
            Priority = ThreadPriority.AboveNormal,
        };
        _renderThread.Start();
    }

    private void RenderLoop()
    {
        var handles = new WaitHandle[] { _frameSignal, _disposeCts.Token.WaitHandle };
        while (!_disposeCts.IsCancellationRequested)
        {
            var signaled = WaitHandle.WaitAny(handles);
            if (signaled == 1 || _disposeCts.IsCancellationRequested)
            {
                break;
            }

            while (!_disposeCts.IsCancellationRequested)
            {
                D3D11VideoFrame? frame;
                lock (_frameLock)
                {
                    frame = _latestFrame;
                    _latestFrame = null;
                }

                if (frame is null)
                {
                    break;
                }

                Render(frame);
            }
        }
    }

    private bool Render(D3D11VideoFrame frame)
    {
        if (!UploadFrame(frame))
        {
            return false;
        }

        if (_frameTexture == IntPtr.Zero || _frameShaderResourceView == IntPtr.Zero)
        {
            return false;
        }

        _panelHost.EnsureSwapChain(_frameWidth, _frameHeight);
        BindPipeline();

        unsafe
        {
            var clearColor = stackalloc float[] { 0.0f, 0.0f, 0.0f, 1.0f };
            var context = _deviceManager.Context;
            LiveKitD3D11Interop.ClearRenderTargetView(context, _panelHost.RenderTargetView, clearColor);
            LiveKitD3D11Interop.RSSetViewports(context, CreateContentViewport());
            LiveKitD3D11Interop.OMSetRenderTargets(context, _panelHost.RenderTargetView);
            LiveKitD3D11Interop.Draw(context, 4, 0);
        }

        _panelHost.Present();
        return true;
    }

    private bool UploadFrame(D3D11VideoFrame frame)
    {
        var sourceWidth = (uint)frame.Width;
        var sourceHeight = (uint)frame.Height;
        var (width, height) = GetUploadSize(sourceWidth, sourceHeight);
        var needsTextureResize = _frameWidth != width || _frameHeight != height || _frameTexture == IntPtr.Zero;
        if (needsTextureResize)
        {
            RecreateFrameTexture(width, height);
            _frameWidth = width;
            _frameHeight = height;
            _lastUploadedFrameIndex = -1;
        }

        if (!needsTextureResize && _lastUploadedFrameIndex == frame.FrameIndex)
        {
            return false;
        }

        unsafe
        {
            fixed (byte* sourcePixelData = frame.Data)
            {
                var pixelData = sourcePixelData;
                var stride = frame.Stride;
                if (width != sourceWidth || height != sourceHeight)
                {
                    pixelData = DownscaleForWarp(sourcePixelData, frame.Width, frame.Height, frame.Stride, (int)width, (int)height);
                    stride = checked((int)width * 4);
                }

                LiveKitD3D11Interop.UpdateSubresource(_deviceManager.Context, _frameTexture, 0, pixelData, (uint)stride, 0);
            }
        }

        _lastUploadedFrameIndex = frame.FrameIndex;
        return true;
    }

    private (uint Width, uint Height) GetUploadSize(uint sourceWidth, uint sourceHeight)
    {
        if (!_deviceManager.IsWarpFallback || sourceWidth <= WarpMaxWidth && sourceHeight <= WarpMaxHeight)
        {
            return (sourceWidth, sourceHeight);
        }

        var scale = Math.Min((float)WarpMaxWidth / sourceWidth, (float)WarpMaxHeight / sourceHeight);
        var width = Math.Max(1u, (uint)Math.Round(sourceWidth * scale));
        var height = Math.Max(1u, (uint)Math.Round(sourceHeight * scale));
        return (width, height);
    }

    private Viewport CreateContentViewport()
    {
        var targetWidth = Math.Max(1u, _panelHost.BufferWidth);
        var targetHeight = Math.Max(1u, _panelHost.BufferHeight);

        if (_frameWidth == 0 || _frameHeight == 0)
        {
            return new Viewport(0, 0, targetWidth, targetHeight);
        }

        var sourceAspect = (float)_frameWidth / _frameHeight;
        var targetAspect = (float)targetWidth / targetHeight;

        float viewportWidth;
        float viewportHeight;

        if (targetAspect > sourceAspect)
        {
            viewportHeight = targetHeight;
            viewportWidth = viewportHeight * sourceAspect;
        }
        else
        {
            viewportWidth = targetWidth;
            viewportHeight = viewportWidth / sourceAspect;
        }

        var offsetX = (targetWidth - viewportWidth) * 0.5f;
        var offsetY = (targetHeight - viewportHeight) * 0.5f;
        return new Viewport(offsetX, offsetY, viewportWidth, viewportHeight);
    }

    private void BindPipeline()
    {
        var context = _deviceManager.Context;
        LiveKitD3D11Interop.PSSetSamplers(context, 0, _samplerState);
        LiveKitD3D11Interop.IASetVertexBuffers(context, 0, _vertexBuffer, _vertexStride, 0);
        LiveKitD3D11Interop.IASetPrimitiveTopology(context, PrimitiveTopology.TriangleStrip);
        LiveKitD3D11Interop.IASetInputLayout(context, _inputLayout);
        LiveKitD3D11Interop.VSSetShader(context, _vertexShader);
        LiveKitD3D11Interop.PSSetShader(context, _pixelShader);
        LiveKitD3D11Interop.PSSetShaderResources(context, 0, _frameShaderResourceView);
    }

    private void RecreateFrameTexture(uint width, uint height)
    {
        LiveKitD3D11Interop.Release(ref _frameShaderResourceView);
        LiveKitD3D11Interop.Release(ref _frameTexture);

        _frameTexture = LiveKitD3D11Interop.CreateTexture2D(
            _deviceManager.Device,
            new Texture2DDescription
            {
                Width = width,
                Height = height,
                MipLevels = 1,
                ArraySize = 1,
                Format = DxgiFormat.R8G8B8A8UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource,
                CpuAccessFlags = CpuAccessFlags.None,
                MiscFlags = ResourceOptionFlags.None,
            });
        _frameShaderResourceView = LiveKitD3D11Interop.CreateShaderResourceView(_deviceManager.Device, _frameTexture);
    }

    private unsafe byte* DownscaleForWarp(byte* sourcePixelData, int sourceWidth, int sourceHeight, int sourceStride, int targetWidth, int targetHeight)
    {
        var targetStride = checked(targetWidth * 4);
        var requiredBytes = checked(targetStride * targetHeight);
        EnsureScaledUploadCapacity(requiredBytes);

        var destinationPixels = (uint*)_scaledUploadBuffer;
        for (var y = 0; y < targetHeight; y++)
        {
            var sourceY = (int)((long)y * sourceHeight / targetHeight);
            var sourceRow = (uint*)(sourcePixelData + sourceY * sourceStride);
            var destinationRow = destinationPixels + (y * targetWidth);
            for (var x = 0; x < targetWidth; x++)
            {
                var sourceX = (int)((long)x * sourceWidth / targetWidth);
                destinationRow[x] = sourceRow[sourceX];
            }
        }

        return (byte*)_scaledUploadBuffer;
    }

    private void EnsureScaledUploadCapacity(int requiredBytes)
    {
        if (_scaledUploadCapacity >= requiredBytes)
        {
            return;
        }

        if (_scaledUploadBuffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_scaledUploadBuffer);
        }

        _scaledUploadBuffer = Marshal.AllocHGlobal(requiredBytes);
        _scaledUploadCapacity = requiredBytes;
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
                    D3DCompileOptimizationLevel3,
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

    private const uint D3DCompileOptimizationLevel3 = 1u << 15;

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
    private struct QuadVertex
    {
        public QuadVertex(float x, float y, float u, float v)
        {
            X = x;
            Y = y;
            U = u;
            V = v;
        }

        public float X;
        public float Y;
        public float U;
        public float V;
    }

    private sealed record D3D11VideoFrame(byte[] Data, int Width, int Height, int Stride, long FrameIndex);

    private const string VertexShaderSource = """
struct VSInput
{
    float2 position : POSITION;
    float2 texCoord : TEXCOORD0;
};

struct PSInput
{
    float4 position : SV_POSITION;
    float2 texCoord : TEXCOORD0;
};

PSInput VSMain(VSInput input)
{
    PSInput output;
    output.position = float4(input.position.xy, 0.0f, 1.0f);
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
}
