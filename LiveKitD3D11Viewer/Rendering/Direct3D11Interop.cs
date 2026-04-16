using System.Runtime.InteropServices;

namespace LiveKitD3D11Viewer.Rendering;

internal static unsafe class Direct3D11Interop
{
    public const uint D3D11SdkVersion = 7;
    public const uint D3D11CreateDeviceBgraSupport = 0x20;
    public const uint DxgiUsageRenderTargetOutput = 0x20;

    public static readonly Guid IidDxgiDevice1 = new("77db970f-6276-48ba-ba28-070143b4392c");
    public static readonly Guid IidDxgiFactory2 = new("50c83a1c-e072-4c48-87b0-3630fa36a6d0");
    public static readonly Guid IidD3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    private const int ContextVsSetConstantBuffersIndex = 7;
    private const int ContextPsSetShaderResourcesIndex = 8;
    private const int ContextPsSetShaderIndex = 9;
    private const int ContextPsSetSamplersIndex = 10;
    private const int ContextVsSetShaderIndex = 11;
    private const int ContextDrawIndex = 13;
    private const int ContextMapIndex = 14;
    private const int ContextUnmapIndex = 15;
    private const int ContextIaSetInputLayoutIndex = 17;
    private const int ContextIaSetVertexBuffersIndex = 18;
    private const int ContextIaSetPrimitiveTopologyIndex = 24;
    private const int ContextOmSetRenderTargetsIndex = 33;
    private const int ContextRsSetViewportsIndex = 44;
    private const int ContextUpdateSubresourceIndex = 48;
    private const int ContextClearRenderTargetViewIndex = 50;

    [DllImport("d3d11.dll", ExactSpelling = true)]
    public static extern int D3D11CreateDevice(
        IntPtr adapter,
        D3DDriverType driverType,
        IntPtr software,
        uint flags,
        D3DFeatureLevel* featureLevels,
        uint featureLevelCount,
        uint sdkVersion,
        IntPtr* device,
        D3DFeatureLevel* featureLevel,
        IntPtr* immediateContext);

    public static void ThrowIfFailed(int hr, string operation)
    {
        if (hr >= 0)
        {
            return;
        }

        var exception = Marshal.GetExceptionForHR(hr, new IntPtr(-1));
        if (exception is not null)
        {
            throw new InvalidOperationException($"{operation} failed with HRESULT 0x{hr:X8}: {exception.Message}", exception);
        }

        throw new InvalidOperationException($"{operation} failed with HRESULT 0x{hr:X8}.");
    }

    public static IntPtr QueryInterface(IntPtr instance, Guid iid)
    {
        IntPtr result;
        var hr = ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)GetVTableEntry(instance, 0))(instance, &iid, &result);
        ThrowIfFailed(hr, $"QueryInterface({iid})");
        return result;
    }

    public static IntPtr GetParent(IntPtr dxgiObject, Guid iid)
    {
        IntPtr result;
        var hr = ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)GetVTableEntry(dxgiObject, 6))(dxgiObject, &iid, &result);
        ThrowIfFailed(hr, "IDXGIObject::GetParent");
        return result;
    }

    public static IntPtr GetAdapter(IntPtr dxgiDevice)
    {
        IntPtr result;
        var hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)GetVTableEntry(dxgiDevice, 7))(dxgiDevice, &result);
        ThrowIfFailed(hr, "IDXGIDevice::GetAdapter");
        return result;
    }

    public static void SetMaximumFrameLatency(IntPtr dxgiDevice1, uint maxLatency)
    {
        var hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, int>)GetVTableEntry(dxgiDevice1, 12))(dxgiDevice1, maxLatency);
        ThrowIfFailed(hr, "IDXGIDevice1::SetMaximumFrameLatency");
    }

    public static IntPtr CreateSwapChainForComposition(IntPtr factory2, IntPtr device, SwapChainDescription1 description)
    {
        IntPtr result;
        var hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, SwapChainDescription1*, IntPtr, IntPtr*, int>)GetVTableEntry(factory2, 24))(
            factory2,
            device,
            &description,
            IntPtr.Zero,
            &result);
        ThrowIfFailed(hr, "IDXGIFactory2::CreateSwapChainForComposition");
        return result;
    }

    public static void ResizeBuffers(IntPtr swapChain, uint bufferCount, uint width, uint height, DxgiFormat format, uint flags)
    {
        var hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, uint, DxgiFormat, uint, int>)GetVTableEntry(swapChain, 13))(
            swapChain,
            bufferCount,
            width,
            height,
            format,
            flags);
        ThrowIfFailed(hr, "IDXGISwapChain::ResizeBuffers");
    }

    public static int TryPresent(IntPtr swapChain, uint syncInterval, uint flags)
    {
        return ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, int>)GetVTableEntry(swapChain, 8))(swapChain, syncInterval, flags);
    }

    public static IntPtr GetBuffer(IntPtr swapChain, uint bufferIndex, Guid iid)
    {
        IntPtr result;
        var hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, Guid*, IntPtr*, int>)GetVTableEntry(swapChain, 9))(
            swapChain,
            bufferIndex,
            &iid,
            &result);
        ThrowIfFailed(hr, "IDXGISwapChain::GetBuffer");
        return result;
    }

    public static IntPtr CreateRenderTargetView(IntPtr device, IntPtr resource)
    {
        IntPtr result;
        var hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, IntPtr*, int>)GetVTableEntry(device, 9))(
            device,
            resource,
            IntPtr.Zero,
            &result);
        ThrowIfFailed(hr, "ID3D11Device::CreateRenderTargetView");
        return result;
    }

    public static IntPtr CreateVertexShader(IntPtr device, byte[] shaderBytecode)
    {
        fixed (byte* shader = shaderBytecode)
        {
            IntPtr result;
            var hr = ((delegate* unmanaged[Stdcall]<IntPtr, void*, nuint, IntPtr, IntPtr*, int>)GetVTableEntry(device, 12))(
                device,
                shader,
                (nuint)shaderBytecode.Length,
                IntPtr.Zero,
                &result);
            ThrowIfFailed(hr, "ID3D11Device::CreateVertexShader");
            return result;
        }
    }

    public static IntPtr CreatePixelShader(IntPtr device, byte[] shaderBytecode)
    {
        fixed (byte* shader = shaderBytecode)
        {
            IntPtr result;
            var hr = ((delegate* unmanaged[Stdcall]<IntPtr, void*, nuint, IntPtr, IntPtr*, int>)GetVTableEntry(device, 15))(
                device,
                shader,
                (nuint)shaderBytecode.Length,
                IntPtr.Zero,
                &result);
            ThrowIfFailed(hr, "ID3D11Device::CreatePixelShader");
            return result;
        }
    }

    public static IntPtr CreateInputLayout(IntPtr device, InputElementDescription[] elements, byte[] shaderBytecode)
    {
        fixed (InputElementDescription* pElements = elements)
        fixed (byte* shader = shaderBytecode)
        {
            IntPtr result;
            var hr = ((delegate* unmanaged[Stdcall]<IntPtr, InputElementDescription*, uint, void*, nuint, IntPtr*, int>)GetVTableEntry(device, 11))(
                device,
                pElements,
                (uint)elements.Length,
                shader,
                (nuint)shaderBytecode.Length,
                &result);
            ThrowIfFailed(hr, "ID3D11Device::CreateInputLayout");
            return result;
        }
    }

    public static IntPtr CreateBuffer(IntPtr device, BufferDescription description, SubresourceData* initialData)
    {
        IntPtr result;
        var hr = ((delegate* unmanaged[Stdcall]<IntPtr, BufferDescription*, SubresourceData*, IntPtr*, int>)GetVTableEntry(device, 3))(
            device,
            &description,
            initialData,
            &result);
        ThrowIfFailed(hr, "ID3D11Device::CreateBuffer");
        return result;
    }

    public static IntPtr CreateSamplerState(IntPtr device, SamplerDescription description)
    {
        IntPtr result;
        var hr = ((delegate* unmanaged[Stdcall]<IntPtr, SamplerDescription*, IntPtr*, int>)GetVTableEntry(device, 23))(
            device,
            &description,
            &result);
        ThrowIfFailed(hr, "ID3D11Device::CreateSamplerState");
        return result;
    }

    public static IntPtr CreateTexture2D(IntPtr device, Texture2DDescription description)
    {
        IntPtr result;
        var hr = ((delegate* unmanaged[Stdcall]<IntPtr, Texture2DDescription*, IntPtr, IntPtr*, int>)GetVTableEntry(device, 5))(
            device,
            &description,
            IntPtr.Zero,
            &result);
        ThrowIfFailed(hr, "ID3D11Device::CreateTexture2D");
        return result;
    }

    public static IntPtr CreateShaderResourceView(IntPtr device, IntPtr resource)
    {
        IntPtr result;
        var hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, IntPtr*, int>)GetVTableEntry(device, 7))(
            device,
            resource,
            IntPtr.Zero,
            &result);
        ThrowIfFailed(hr, "ID3D11Device::CreateShaderResourceView");
        return result;
    }

    public static void ClearRenderTargetView(IntPtr context, IntPtr renderTargetView, float* color)
    {
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, float*, void>)GetVTableEntry(context, ContextClearRenderTargetViewIndex))(context, renderTargetView, color);
    }

    public static void RSSetViewports(IntPtr context, Viewport viewport)
    {
        ((delegate* unmanaged[Stdcall]<IntPtr, uint, Viewport*, void>)GetVTableEntry(context, ContextRsSetViewportsIndex))(context, 1, &viewport);
    }

    public static void OMSetRenderTargets(IntPtr context, IntPtr renderTargetView)
    {
        IntPtr local = renderTargetView;
        ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, IntPtr, void>)GetVTableEntry(context, ContextOmSetRenderTargetsIndex))(context, 1, &local, IntPtr.Zero);
    }

    public static void PSSetShaderResources(IntPtr context, uint startSlot, IntPtr shaderResourceView)
    {
        IntPtr local = shaderResourceView;
        ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, void>)GetVTableEntry(context, ContextPsSetShaderResourcesIndex))(context, startSlot, 1, &local);
    }

    public static void PSSetSamplers(IntPtr context, uint startSlot, IntPtr samplerState)
    {
        IntPtr local = samplerState;
        ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, void>)GetVTableEntry(context, ContextPsSetSamplersIndex))(context, startSlot, 1, &local);
    }

    public static void IASetVertexBuffers(IntPtr context, uint startSlot, IntPtr vertexBuffer, uint stride, uint offset)
    {
        IntPtr local = vertexBuffer;
        ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, uint*, uint*, void>)GetVTableEntry(context, ContextIaSetVertexBuffersIndex))(
            context,
            startSlot,
            1,
            &local,
            &stride,
            &offset);
    }

    public static void IASetPrimitiveTopology(IntPtr context, PrimitiveTopology topology)
    {
        ((delegate* unmanaged[Stdcall]<IntPtr, PrimitiveTopology, void>)GetVTableEntry(context, ContextIaSetPrimitiveTopologyIndex))(context, topology);
    }

    public static void IASetInputLayout(IntPtr context, IntPtr inputLayout)
    {
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, void>)GetVTableEntry(context, ContextIaSetInputLayoutIndex))(context, inputLayout);
    }

    public static void VSSetConstantBuffers(IntPtr context, uint startSlot, IntPtr constantBuffer)
    {
        IntPtr local = constantBuffer;
        ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, void>)GetVTableEntry(context, ContextVsSetConstantBuffersIndex))(context, startSlot, 1, &local);
    }

    public static void VSSetShader(IntPtr context, IntPtr vertexShader)
    {
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, uint, void>)GetVTableEntry(context, ContextVsSetShaderIndex))(context, vertexShader, IntPtr.Zero, 0);
    }

    public static void PSSetShader(IntPtr context, IntPtr pixelShader)
    {
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, uint, void>)GetVTableEntry(context, ContextPsSetShaderIndex))(context, pixelShader, IntPtr.Zero, 0);
    }

    public static void Draw(IntPtr context, uint vertexCount, uint startVertexLocation)
    {
        ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, void>)GetVTableEntry(context, ContextDrawIndex))(context, vertexCount, startVertexLocation);
    }

    public static MappedSubresource Map(IntPtr context, IntPtr resource, uint subresource, MapType mapType, MapFlags mapFlags)
    {
        MappedSubresource mapped;
        var hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, MapType, MapFlags, MappedSubresource*, int>)GetVTableEntry(context, ContextMapIndex))(
            context,
            resource,
            subresource,
            mapType,
            mapFlags,
            &mapped);
        ThrowIfFailed(hr, "ID3D11DeviceContext::Map");
        return mapped;
    }

    public static void Unmap(IntPtr context, IntPtr resource, uint subresource)
    {
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, void>)GetVTableEntry(context, ContextUnmapIndex))(context, resource, subresource);
    }

    public static void UpdateSubresource(IntPtr context, IntPtr resource, uint subresource, void* data, uint rowPitch, uint depthPitch)
    {
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, IntPtr, void*, uint, uint, void>)GetVTableEntry(context, ContextUpdateSubresourceIndex))(
            context,
            resource,
            subresource,
            IntPtr.Zero,
            data,
            rowPitch,
            depthPitch);
    }

    public static void Release(ref IntPtr instance)
    {
        if (instance == IntPtr.Zero)
        {
            return;
        }

        ((delegate* unmanaged[Stdcall]<IntPtr, uint>)GetVTableEntry(instance, 2))(instance);
        instance = IntPtr.Zero;
    }

    private static void* GetVTableEntry(IntPtr instance, int index)
    {
        return ((void**)*(void**)instance)[index];
    }
}

internal enum D3DDriverType : uint
{
    Hardware = 1,
    Warp = 5,
}

public enum D3DFeatureLevel : uint
{
    Level101 = 0xa100,
    Level110 = 0xb000,
    Level111 = 0xb100,
}

[Flags]
internal enum BindFlags : uint
{
    VertexBuffer = 0x1,
    ConstantBuffer = 0x4,
    ShaderResource = 0x8,
}

internal enum ResourceUsage : uint
{
    Default = 0,
    Immutable = 1,
    Dynamic = 2,
}

internal enum CpuAccessFlags : uint
{
    None = 0,
    Write = 0x10000,
}

internal enum ResourceOptionFlags : uint
{
    None = 0,
}

internal enum Filter : uint
{
    MinMagMipLinear = 0x15,
}

internal enum TextureAddressMode : uint
{
    Clamp = 3,
}

internal enum ComparisonFunction : uint
{
    Never = 1,
}

internal enum PrimitiveTopology : uint
{
    TriangleStrip = 5,
}

internal enum DxgiFormat : uint
{
    R32G32Float = 16,
    R8G8B8A8UNorm = 28,
}

internal enum Scaling : uint
{
    Stretch = 0,
}

internal enum SwapEffect : uint
{
    FlipSequential = 3,
}

internal enum AlphaMode : uint
{
    Ignore = 3,
}

internal enum MapType : uint
{
    WriteDiscard = 4,
}

[Flags]
internal enum MapFlags : uint
{
    None = 0,
}

[StructLayout(LayoutKind.Sequential)]
internal struct SampleDescription
{
    public uint Count;
    public uint Quality;

    public SampleDescription(uint count, uint quality)
    {
        Count = count;
        Quality = quality;
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct SwapChainDescription1
{
    public uint Width;
    public uint Height;
    public DxgiFormat Format;
    [MarshalAs(UnmanagedType.Bool)] public bool Stereo;
    public SampleDescription SampleDescription;
    public uint BufferUsage;
    public uint BufferCount;
    public Scaling Scaling;
    public SwapEffect SwapEffect;
    public AlphaMode AlphaMode;
    public uint Flags;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct InputElementDescription
{
    public sbyte* SemanticName;
    public uint SemanticIndex;
    public DxgiFormat Format;
    public uint InputSlot;
    public uint AlignedByteOffset;
    public uint InputSlotClass;
    public uint InstanceDataStepRate;
}

[StructLayout(LayoutKind.Sequential)]
internal struct BufferDescription
{
    public uint ByteWidth;
    public ResourceUsage Usage;
    public BindFlags BindFlags;
    public CpuAccessFlags CpuAccessFlags;
    public ResourceOptionFlags MiscFlags;
    public uint StructureByteStride;

    public BufferDescription(uint byteWidth, BindFlags bindFlags, ResourceUsage usage, CpuAccessFlags cpuAccessFlags, ResourceOptionFlags miscFlags, uint structureByteStride)
    {
        ByteWidth = byteWidth;
        BindFlags = bindFlags;
        Usage = usage;
        CpuAccessFlags = cpuAccessFlags;
        MiscFlags = miscFlags;
        StructureByteStride = structureByteStride;
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct SubresourceData
{
    public IntPtr SysMem;
    public uint SysMemPitch;
    public uint SysMemSlicePitch;

    public SubresourceData(IntPtr sysMem, uint sysMemPitch, uint sysMemSlicePitch)
    {
        SysMem = sysMem;
        SysMemPitch = sysMemPitch;
        SysMemSlicePitch = sysMemSlicePitch;
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct SamplerDescription
{
    public Filter Filter;
    public TextureAddressMode AddressU;
    public TextureAddressMode AddressV;
    public TextureAddressMode AddressW;
    public float MipLodBias;
    public uint MaxAnisotropy;
    public ComparisonFunction ComparisonFunc;
    public float BorderColorR;
    public float BorderColorG;
    public float BorderColorB;
    public float BorderColorA;
    public float MinLod;
    public float MaxLod;
}

[StructLayout(LayoutKind.Sequential)]
internal struct Texture2DDescription
{
    public uint Width;
    public uint Height;
    public uint MipLevels;
    public uint ArraySize;
    public DxgiFormat Format;
    public SampleDescription SampleDescription;
    public ResourceUsage Usage;
    public BindFlags BindFlags;
    public CpuAccessFlags CpuAccessFlags;
    public ResourceOptionFlags MiscFlags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MappedSubresource
{
    public IntPtr DataPointer;
    public uint RowPitch;
    public uint DepthPitch;
}

[StructLayout(LayoutKind.Sequential)]
internal struct Viewport
{
    public float TopLeftX;
    public float TopLeftY;
    public float Width;
    public float Height;
    public float MinDepth;
    public float MaxDepth;

    public Viewport(float topLeftX, float topLeftY, float width, float height, float minDepth = 0.0f, float maxDepth = 1.0f)
    {
        TopLeftX = topLeftX;
        TopLeftY = topLeftY;
        Width = width;
        Height = height;
        MinDepth = minDepth;
        MaxDepth = maxDepth;
    }
}
