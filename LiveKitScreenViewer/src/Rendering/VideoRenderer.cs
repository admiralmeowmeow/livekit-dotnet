using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using LiveKitScreenViewer.Diagnostics;
using LiveKitScreenViewer.Frames;

namespace LiveKitScreenViewer.Rendering;

public sealed class VideoRenderer : IDisposable
{
    private const uint WarpMaxWidth = 1920;
    private const uint WarpMaxHeight = 1080;
    private readonly DxDeviceManager _deviceManager;
    private readonly SwapChainPanelHost _panelHost;
    private readonly IntPtr _vertexShader;
    private readonly IntPtr _pixelShader;
    private readonly IntPtr _inputLayout;
    private readonly IntPtr _vertexBuffer;
    private readonly IntPtr _transformBuffer;
    private readonly IntPtr _samplerState;
    private readonly uint _vertexStride = (uint)Marshal.SizeOf<QuadVertex>();
    private readonly ContentScaleMode _contentScaleMode = ContentScaleMode.Fit;
    private readonly object _statsLock = new();
    private IntPtr _defaultFrameTexture;
    private IntPtr _defaultFrameShaderResourceView;
    private IntPtr _dynamicFrameTexture;
    private IntPtr _dynamicFrameShaderResourceView;
    private IntPtr _activeFrameTexture;
    private IntPtr _activeFrameShaderResourceView;
    private uint _frameWidth;
    private uint _frameHeight;
    private IntPtr _scaledUploadBuffer;
    private int _scaledUploadCapacity;
    private long _lastUploadedFrameIndex = -1;
    private VideoFrameSource? _lastUploadedFrameSource;
    private double _currentFramesPerSecond;
    private int _framesPresentedSinceLastSample;
    private DateTime _lastFpsSampleUtc = DateTime.UtcNow;
    private int _staticPipelineBound;
    private UploadMode _selectedUploadMode = UploadMode.PendingBenchmark;
    private int _benchmarkFramesRemaining = 24;
    private double _updateSubresourceUploadTotalMs;
    private int _updateSubresourceUploadSamples;
    private double _mapUploadTotalMs;
    private int _mapUploadSamples;

    public VideoRenderer(DxDeviceManager deviceManager, SwapChainPanelHost panelHost)
    {
        _deviceManager = deviceManager;
        _panelHost = panelHost;

        var vertexShaderBytes = CompileShader(VertexShaderSource, "VSMain", "vs_4_0");
        var pixelShaderBytes = CompileShader(PixelShaderSource, "PSMain", "ps_4_0");

        _vertexShader = Direct3D11Interop.CreateVertexShader(_deviceManager.Device, vertexShaderBytes);
        _pixelShader = Direct3D11Interop.CreatePixelShader(_deviceManager.Device, pixelShaderBytes);

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

                _inputLayout = Direct3D11Interop.CreateInputLayout(_deviceManager.Device, inputElements, vertexShaderBytes);
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
                _vertexBuffer = Direct3D11Interop.CreateBuffer(_deviceManager.Device, vertexBufferDescription, &vertexSubresource);
            }
        }

        _samplerState = Direct3D11Interop.CreateSamplerState(
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

        var transformBufferDescription = new BufferDescription(
            (uint)Marshal.SizeOf<TransformConstants>(),
            BindFlags.ConstantBuffer,
            ResourceUsage.Default,
            CpuAccessFlags.None,
            ResourceOptionFlags.None,
            0);

        unsafe
        {
            _transformBuffer = Direct3D11Interop.CreateBuffer(_deviceManager.Device, transformBufferDescription, (SubresourceData*)null);
        }

        UpdateTransformBufferIdentity();
        BindStaticPipeline();
    }

    public double CurrentFramesPerSecond
    {
        get
        {
            lock (_statsLock)
            {
                return _currentFramesPerSecond;
            }
        }
    }

    public string ContentScaleLabel => _contentScaleMode == ContentScaleMode.Fill
        ? "aspect fill"
        : "aspect fit";

    public string UploadModeLabel => _selectedUploadMode switch
    {
        UploadMode.MapWriteDiscard => "map/unmap",
        UploadMode.UpdateSubresource => "UpdateSubresource",
        _ => "benchmarking",
    };

    public bool Render(VideoFrame frame)
    {
        frame.Timings.MarkRenderStart();
        if (!UploadFrame(frame))
        {
            return false;
        }

        if (_activeFrameTexture == IntPtr.Zero || _activeFrameShaderResourceView == IntPtr.Zero)
        {
            return false;
        }

        _panelHost.EnsureSwapChain(_frameWidth, _frameHeight);
        BindStaticPipeline();

        unsafe
        {
            var clearColor = stackalloc float[] { 0.0f, 0.0f, 0.0f, 1.0f };
            var context = _deviceManager.Context;
            Direct3D11Interop.ClearRenderTargetView(context, _panelHost.RenderTargetView, clearColor);
            Direct3D11Interop.RSSetViewports(context, CreateContentViewport());
            Direct3D11Interop.OMSetRenderTargets(context, _panelHost.RenderTargetView);
            frame.Timings.MarkDrawStart();
            Direct3D11Interop.Draw(context, 4, 0);
        }

        _panelHost.Present();
        frame.Timings.MarkPresentEnd();
        UpdatePresentedFramesPerSecond();
        return true;
    }

    public void Dispose()
    {
        Direct3D11Interop.Release(ref _defaultFrameShaderResourceView);
        Direct3D11Interop.Release(ref _defaultFrameTexture);
        Direct3D11Interop.Release(ref _dynamicFrameShaderResourceView);
        Direct3D11Interop.Release(ref _dynamicFrameTexture);
        if (_scaledUploadBuffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_scaledUploadBuffer);
            _scaledUploadBuffer = IntPtr.Zero;
            _scaledUploadCapacity = 0;
        }

        var transformBuffer = _transformBuffer;
        Direct3D11Interop.Release(ref transformBuffer);

        var samplerState = _samplerState;
        Direct3D11Interop.Release(ref samplerState);

        var vertexBuffer = _vertexBuffer;
        Direct3D11Interop.Release(ref vertexBuffer);

        var inputLayout = _inputLayout;
        Direct3D11Interop.Release(ref inputLayout);

        var pixelShader = _pixelShader;
        Direct3D11Interop.Release(ref pixelShader);

        var vertexShader = _vertexShader;
        Direct3D11Interop.Release(ref vertexShader);
    }

    private bool UploadFrame(VideoFrame frame)
    {
        var sourceWidth = (uint)frame.Width;
        var sourceHeight = (uint)frame.Height;
        var (width, height) = GetUploadSize(sourceWidth, sourceHeight);
        var needsTextureResize = _frameWidth != width || _frameHeight != height || _defaultFrameTexture == IntPtr.Zero;
        if (needsTextureResize)
        {
            RecreateFrameTextures(width, height);
            _frameWidth = width;
            _frameHeight = height;
            _lastUploadedFrameIndex = -1;
            _lastUploadedFrameSource = null;
            ResetUploadModeSelection();
            _updateSubresourceUploadSamples = 0;
            _updateSubresourceUploadTotalMs = 0;
            _mapUploadSamples = 0;
            _mapUploadTotalMs = 0;
        }

        if (!needsTextureResize &&
            _lastUploadedFrameIndex == frame.FrameIndex &&
            _lastUploadedFrameSource == frame.Source)
        {
            return false;
        }

        var uploadMode = ResolveUploadMode();
        frame.Timings.MarkUploadStart();
        var uploadStart = StopwatchTickHelpers.GetTimestamp();

        unsafe
        {
            var sourcePixelData = (byte*)frame.DataPointer;
            byte* pixelData = sourcePixelData;
            var stride = frame.Stride;
            if (pixelData == null)
            {
                return false;
            }

            if (width != sourceWidth || height != sourceHeight)
            {
                pixelData = DownscaleForWarp(sourcePixelData, frame.Width, frame.Height, (int)width, (int)height);
                stride = checked((int)width * 4);
            }

            if (uploadMode == UploadMode.MapWriteDiscard)
            {
                UploadWithMap(pixelData, (int)height, stride);
            }
            else
            {
                UploadWithUpdateSubresource(pixelData, stride);
            }
        }

        frame.Timings.MarkUploadEnd();
        TrackUploadBenchmark(uploadMode, StopwatchTickHelpers.ToMilliseconds(StopwatchTickHelpers.GetTimestamp() - uploadStart));
        _lastUploadedFrameIndex = frame.FrameIndex;
        _lastUploadedFrameSource = frame.Source;
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

        if (_contentScaleMode == ContentScaleMode.Fill)
        {
            if (targetAspect > sourceAspect)
            {
                viewportWidth = targetWidth;
                viewportHeight = viewportWidth / sourceAspect;
            }
            else
            {
                viewportHeight = targetHeight;
                viewportWidth = viewportHeight * sourceAspect;
            }
        }
        else if (targetAspect > sourceAspect)
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

    private void UpdateTransformBufferIdentity()
    {
        var transform = new TransformConstants(1.0f, 1.0f);
        unsafe
        {
            Direct3D11Interop.UpdateSubresource(_deviceManager.Context, _transformBuffer, 0, &transform, 0, 0);
        }
    }

    private void BindStaticPipeline()
    {
        if (Interlocked.Exchange(ref _staticPipelineBound, 1) != 0)
        {
            if (_activeFrameShaderResourceView != IntPtr.Zero)
            {
                Direct3D11Interop.PSSetShaderResources(_deviceManager.Context, 0, _activeFrameShaderResourceView);
            }

            return;
        }

        var context = _deviceManager.Context;
        Direct3D11Interop.PSSetSamplers(context, 0, _samplerState);
        Direct3D11Interop.IASetVertexBuffers(context, 0, _vertexBuffer, _vertexStride, 0);
        Direct3D11Interop.IASetPrimitiveTopology(context, PrimitiveTopology.TriangleStrip);
        Direct3D11Interop.IASetInputLayout(context, _inputLayout);
        Direct3D11Interop.VSSetConstantBuffers(context, 0, _transformBuffer);
        Direct3D11Interop.VSSetShader(context, _vertexShader);
        Direct3D11Interop.PSSetShader(context, _pixelShader);

        if (_activeFrameShaderResourceView != IntPtr.Zero)
        {
            Direct3D11Interop.PSSetShaderResources(context, 0, _activeFrameShaderResourceView);
        }
    }

    private void RecreateFrameTextures(uint width, uint height)
    {
        Direct3D11Interop.Release(ref _defaultFrameShaderResourceView);
        Direct3D11Interop.Release(ref _defaultFrameTexture);
        Direct3D11Interop.Release(ref _dynamicFrameShaderResourceView);
        Direct3D11Interop.Release(ref _dynamicFrameTexture);

        _defaultFrameTexture = Direct3D11Interop.CreateTexture2D(
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
            });
        _defaultFrameShaderResourceView = Direct3D11Interop.CreateShaderResourceView(_deviceManager.Device, _defaultFrameTexture);

        _dynamicFrameTexture = Direct3D11Interop.CreateTexture2D(
            _deviceManager.Device,
            new Texture2DDescription
            {
                Width = width,
                Height = height,
                MipLevels = 1,
                ArraySize = 1,
                Format = DxgiFormat.R8G8B8A8UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Dynamic,
                BindFlags = BindFlags.ShaderResource,
                CpuAccessFlags = CpuAccessFlags.Write,
            });
        _dynamicFrameShaderResourceView = Direct3D11Interop.CreateShaderResourceView(_deviceManager.Device, _dynamicFrameTexture);
        _activeFrameTexture = IntPtr.Zero;
        _activeFrameShaderResourceView = IntPtr.Zero;
    }

    private unsafe void UploadWithUpdateSubresource(byte* pixelData, int stride)
    {
        _activeFrameTexture = _defaultFrameTexture;
        _activeFrameShaderResourceView = _defaultFrameShaderResourceView;
        Direct3D11Interop.UpdateSubresource(_deviceManager.Context, _defaultFrameTexture, 0, pixelData, (uint)stride, 0);
        Direct3D11Interop.PSSetShaderResources(_deviceManager.Context, 0, _activeFrameShaderResourceView);
    }

    private unsafe void UploadWithMap(byte* pixelData, int height, int stride)
    {
        _activeFrameTexture = _dynamicFrameTexture;
        _activeFrameShaderResourceView = _dynamicFrameShaderResourceView;

        var mapped = Direct3D11Interop.Map(_deviceManager.Context, _dynamicFrameTexture, 0, MapType.WriteDiscard, MapFlags.None);
        try
        {
            var sourceRow = pixelData;
            var destinationRow = (byte*)mapped.DataPointer;
            for (var row = 0; row < height; row++)
            {
                Buffer.MemoryCopy(sourceRow, destinationRow, mapped.RowPitch, stride);
                sourceRow += stride;
                destinationRow += mapped.RowPitch;
            }
        }
        finally
        {
            Direct3D11Interop.Unmap(_deviceManager.Context, _dynamicFrameTexture, 0);
        }

        Direct3D11Interop.PSSetShaderResources(_deviceManager.Context, 0, _activeFrameShaderResourceView);
    }

    private UploadMode ResolveUploadMode()
    {
        if (_selectedUploadMode != UploadMode.PendingBenchmark)
        {
            return _selectedUploadMode;
        }

        if (_benchmarkFramesRemaining <= 0)
        {
            var updateAverage = _updateSubresourceUploadSamples == 0 ? double.MaxValue : _updateSubresourceUploadTotalMs / _updateSubresourceUploadSamples;
            var mapAverage = _mapUploadSamples == 0 ? double.MaxValue : _mapUploadTotalMs / _mapUploadSamples;
            _selectedUploadMode = mapAverage < updateAverage
                ? UploadMode.MapWriteDiscard
                : UploadMode.UpdateSubresource;
            return _selectedUploadMode;
        }

        return (_benchmarkFramesRemaining & 1) == 0
            ? UploadMode.UpdateSubresource
            : UploadMode.MapWriteDiscard;
    }

    private void ResetUploadModeSelection()
    {
        if (_deviceManager.IsWarpFallback)
        {
            _selectedUploadMode = UploadMode.UpdateSubresource;
            _benchmarkFramesRemaining = 0;
            return;
        }

        _selectedUploadMode = UploadMode.PendingBenchmark;
        _benchmarkFramesRemaining = 8;
    }

    private void TrackUploadBenchmark(UploadMode uploadMode, double elapsedMilliseconds)
    {
        if (_selectedUploadMode == UploadMode.PendingBenchmark)
        {
            if (uploadMode == UploadMode.MapWriteDiscard)
            {
                _mapUploadTotalMs += elapsedMilliseconds;
                _mapUploadSamples++;
            }
            else
            {
                _updateSubresourceUploadTotalMs += elapsedMilliseconds;
                _updateSubresourceUploadSamples++;
            }

            _benchmarkFramesRemaining--;
            if (_benchmarkFramesRemaining <= 0)
            {
                ResolveUploadMode();
            }
        }
    }

    private void UpdatePresentedFramesPerSecond()
    {
        lock (_statsLock)
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
    }

    private unsafe byte* DownscaleForWarp(byte* sourcePixelData, int sourceWidth, int sourceHeight, int targetWidth, int targetHeight)
    {
        var targetStride = checked(targetWidth * 4);
        var requiredBytes = checked(targetStride * targetHeight);
        EnsureScaledUploadCapacity(requiredBytes);

        var sourcePixels = (uint*)sourcePixelData;
        var destinationPixels = (uint*)_scaledUploadBuffer;
        for (var y = 0; y < targetHeight; y++)
        {
            var sourceY = (int)((long)y * sourceHeight / targetHeight);
            var sourceRow = sourcePixels + (sourceY * sourceWidth);
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

    [StructLayout(LayoutKind.Sequential)]
    private struct TransformConstants
    {
        public TransformConstants(float scaleX, float scaleY)
        {
            ScaleX = scaleX;
            ScaleY = scaleY;
            Padding0 = 0f;
            Padding1 = 0f;
        }

        public float ScaleX;

        public float ScaleY;

        public float Padding0;

        public float Padding1;
    }

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

cbuffer TransformBuffer : register(b0)
{
    float2 contentScale;
    float2 _padding;
}

PSInput VSMain(VSInput input)
{
    PSInput output;
    output.position = float4(input.position.xy * contentScale, 0.0f, 1.0f);
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

public enum ContentScaleMode
{
    Fit,
    Fill,
}

internal enum UploadMode
{
    PendingBenchmark,
    UpdateSubresource,
    MapWriteDiscard,
}
