using System.Runtime.InteropServices;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace LiveKitScreenShareHost.Capture;

internal sealed class WgcScreenCapturer : ICaptureBackend
{
    private readonly D3D11CaptureDevice _captureDevice;
    private readonly GraphicsCaptureItem _captureItem;
    private readonly Direct3D11CaptureFramePool _framePool;
    private readonly GraphicsCaptureSession _session;
    private readonly AutoResetEvent _frameAvailable = new(false);
    private readonly object _sync = new();
    private readonly RgbaFramePool _framePoolRgba;
    private IntPtr _stagingTexture;
    private Direct3D11CaptureFrame? _latestFrame;
    private SizeInt32 _currentSize;
    private bool _disposed;

    public static bool IsSupported => GraphicsCaptureSession.IsSupported();

    public WgcScreenCapturer(DisplayOption display)
    {
        try
        {
            _captureDevice = new D3D11CaptureDevice();
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException("WGC failed while creating the D3D11 capture device.", exception);
        }

        try
        {
            _captureItem = CreateItemForMonitor(display.MonitorHandle);
        }
        catch (Exception exception)
        {
            _captureDevice.Dispose();
            throw new InvalidOperationException("WGC failed while creating the monitor capture item.", exception);
        }

        _currentSize = _captureItem.Size;
        _framePoolRgba = new RgbaFramePool(_currentSize.Width * _currentSize.Height * 4);
        _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            _captureDevice.Device,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            2,
            _currentSize);
        _framePool.FrameArrived += OnFrameArrived;
        _captureItem.Closed += OnCaptureItemClosed;
        _session = _framePool.CreateCaptureSession(_captureItem);
        EnsureStagingTexture(_currentSize.Width, _currentSize.Height);
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            _session.IsCursorCaptureEnabled = true;
        }
        _session.StartCapture();
    }

    public System.Drawing.Size Resolution => new(_currentSize.Width, _currentSize.Height);

    public RgbaFramePool FramePool => _framePoolRgba;

    public string BackendName => "WGC";

    public bool IsFrameDriven => true;

    public CapturedFrame CaptureFrame()
    {
        Direct3D11CaptureFrame? frame = null;
        while (frame is null)
        {
            ThrowIfDisposed();
            lock (_sync)
            {
                frame = _latestFrame;
                _latestFrame = null;
            }

            if (frame is not null)
            {
                break;
            }

            _frameAvailable.WaitOne(250);
        }

        try
        {
            return CaptureMappedFrame(frame);
        }
        finally
        {
            frame.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _framePool.FrameArrived -= OnFrameArrived;
        lock (_sync)
        {
            _latestFrame?.Dispose();
            _latestFrame = null;
        }

        _session.Dispose();
        _framePool.Dispose();
        _captureItem.Closed -= OnCaptureItemClosed;
        if (_stagingTexture != IntPtr.Zero)
        {
            D3D11CaptureInterop.Release(ref _stagingTexture);
        }
        _captureDevice.Dispose();
        _framePoolRgba.Dispose();
        _frameAvailable.Dispose();
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        if (_disposed)
        {
            return;
        }

        Direct3D11CaptureFrame? frame = null;
        try
        {
            frame = sender.TryGetNextFrame();
            if (frame is null)
            {
                return;
            }

            var contentSize = frame.ContentSize;
            if (contentSize.Width > 0 &&
                contentSize.Height > 0 &&
                (contentSize.Width != _currentSize.Width || contentSize.Height != _currentSize.Height))
            {
                _currentSize = contentSize;
                sender.Recreate(_captureDevice.Device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _currentSize);
                EnsureStagingTexture(_currentSize.Width, _currentSize.Height);
            }

            lock (_sync)
            {
                var replaced = _latestFrame;
                _latestFrame = frame;
                frame = null;
                replaced?.Dispose();
            }

            _frameAvailable.Set();
        }
        finally
        {
            frame?.Dispose();
        }
    }

    private void OnCaptureItemClosed(GraphicsCaptureItem sender, object args)
    {
        _disposed = true;
        _frameAvailable.Set();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private CapturedFrame CaptureMappedFrame(Direct3D11CaptureFrame frame)
    {
        var surfaceAccess = frame.Surface.As<IDirect3DDxgiInterfaceAccess>();
        IntPtr sourceTexture = IntPtr.Zero;
        try
        {
            sourceTexture = surfaceAccess.GetInterface(D3D11CaptureInterop.IidD3D11Texture2D);
            D3D11CaptureInterop.CopyResource(_captureDevice.ContextPointer, _stagingTexture, sourceTexture);
            var mapped = D3D11CaptureInterop.Map(_captureDevice.ContextPointer, _stagingTexture, 0, MapType.Read, MapFlags.None);
            return new CapturedFrame(new MappedTextureLease(_captureDevice.ContextPointer, _stagingTexture), _currentSize.Width, _currentSize.Height, (int)mapped.RowPitch, mapped.DataPointer);
        }
        finally
        {
            if (sourceTexture != IntPtr.Zero)
            {
                D3D11CaptureInterop.Release(ref sourceTexture);
            }

            Marshal.ReleaseComObject(surfaceAccess);
        }
    }

    private static GraphicsCaptureItem CreateItemForMonitor(IntPtr monitorHandle)
    {
        var factory = WinRtCaptureInterop.GetGraphicsCaptureItemInterop();
        try
        {
            var itemPointer = WinRtCaptureInterop.CreateItemForMonitor(factory, monitorHandle, WinRtCaptureInterop.GraphicsCaptureItemIid);
            return MarshalInspectable<GraphicsCaptureItem>.FromAbi(itemPointer);
        }
        finally
        {
            WinRtCaptureInterop.Release(ref factory);
        }
    }

    [ComImport]
    [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        IntPtr GetInterface([In] ref Guid iid);
    }

    private void EnsureStagingTexture(int width, int height)
    {
        if (_stagingTexture != IntPtr.Zero)
        {
            D3D11CaptureInterop.Release(ref _stagingTexture);
        }

        _stagingTexture = D3D11CaptureInterop.CreateTexture2D(
            _captureDevice.DevicePointer,
            new Texture2DDescription
            {
                Width = (uint)width,
                Height = (uint)height,
                MipLevels = 1,
                ArraySize = 1,
                Format = DxgiFormat.B8G8R8A8UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CpuAccessFlags = CpuAccessFlags.Read,
                MiscFlags = ResourceOptionFlags.None,
            });
    }

    private sealed class MappedTextureLease : IDisposable
    {
        private readonly IntPtr _context;
        private readonly IntPtr _texture;
        private bool _disposed;

        public MappedTextureLease(IntPtr context, IntPtr texture)
        {
            _context = context;
            _texture = texture;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            D3D11CaptureInterop.Unmap(_context, _texture, 0);
        }
    }
}

internal static unsafe class WinRtCaptureInterop
{
    private static readonly Guid IidGraphicsCaptureItemInterop = new("3628e81b-3cac-4c60-b7f4-23ce0e0c3356");
    private static readonly Guid IidGraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly IntPtr CombaseModule = NativeLibrary.Load("combase.dll");
    private static readonly WindowsCreateStringDelegate WindowsCreateStringImpl =
        Marshal.GetDelegateForFunctionPointer<WindowsCreateStringDelegate>(NativeLibrary.GetExport(CombaseModule, "WindowsCreateString"));
    private static readonly WindowsDeleteStringDelegate WindowsDeleteStringImpl =
        Marshal.GetDelegateForFunctionPointer<WindowsDeleteStringDelegate>(NativeLibrary.GetExport(CombaseModule, "WindowsDeleteString"));
    private static readonly RoGetActivationFactoryDelegate RoGetActivationFactoryImpl =
        Marshal.GetDelegateForFunctionPointer<RoGetActivationFactoryDelegate>(NativeLibrary.GetExport(CombaseModule, "RoGetActivationFactory"));

    public static IntPtr GetGraphicsCaptureItemInterop()
    {
        var className = "Windows.Graphics.Capture.GraphicsCaptureItem";
        var iid = IidGraphicsCaptureItemInterop;
        IntPtr hstring = IntPtr.Zero;
        try
        {
            ThrowIfFailed(WindowsCreateStringImpl(className, className.Length, out hstring), "WindowsCreateString(GraphicsCaptureItem)");
            IntPtr activationFactory;
            ThrowIfFailed(RoGetActivationFactoryImpl(hstring, ref iid, out activationFactory), "RoGetActivationFactory(GraphicsCaptureItem)");
            return activationFactory;
        }
        finally
        {
            if (hstring != IntPtr.Zero)
            {
                WindowsDeleteStringImpl(hstring);
            }
        }
    }

    public static Guid GraphicsCaptureItemIid => IidGraphicsCaptureItem;

    public static IntPtr CreateItemForMonitor(IntPtr activationFactory, IntPtr monitorHandle, Guid iid)
    {
        IntPtr item;
        var hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)GetVTableEntry(activationFactory, 4))(
            activationFactory,
            monitorHandle,
            &iid,
            &item);
        ThrowIfFailed(hr, "IGraphicsCaptureItemInterop::CreateForMonitor");
        return item;
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

    private static void* GetVTableEntry(IntPtr instance, int index)
    {
        return ((void**)*(void**)instance)[index];
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    private delegate int WindowsCreateStringDelegate([MarshalAs(UnmanagedType.LPWStr)] string sourceString, int length, out IntPtr hstring);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int WindowsDeleteStringDelegate(IntPtr hstring);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int RoGetActivationFactoryDelegate(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);
}

internal sealed class D3D11CaptureDevice : IDisposable
{
    public D3D11CaptureDevice()
    {
        var featureLevels = new[]
        {
            D3DFeatureLevel.Level111,
            D3DFeatureLevel.Level110,
            D3DFeatureLevel.Level101,
        };

        unsafe
        {
            fixed (D3DFeatureLevel* requestedFeatureLevels = featureLevels)
            {
                IntPtr device;
                IntPtr context;
                D3DFeatureLevel actualFeatureLevel;

                var hr = D3D11CaptureInterop.D3D11CreateDevice(
                    IntPtr.Zero,
                    D3DDriverType.Hardware,
                    IntPtr.Zero,
                    D3D11CaptureInterop.D3D11CreateDeviceBgraSupport,
                    requestedFeatureLevels,
                    (uint)featureLevels.Length,
                    D3D11CaptureInterop.D3D11SdkVersion,
                    &device,
                    &actualFeatureLevel,
                    &context);

                if (hr < 0)
                {
                    D3D11CaptureInterop.ThrowIfFailed(
                        D3D11CaptureInterop.D3D11CreateDevice(
                            IntPtr.Zero,
                            D3DDriverType.Warp,
                            IntPtr.Zero,
                            D3D11CaptureInterop.D3D11CreateDeviceBgraSupport,
                            requestedFeatureLevels,
                            (uint)featureLevels.Length,
                            D3D11CaptureInterop.D3D11SdkVersion,
                            &device,
                            &actualFeatureLevel,
                            &context),
                        "D3D11CreateDevice");
                }

                DevicePointer = device;
                ContextPointer = context;
            }
        }

        var dxgiDevice = D3D11CaptureInterop.QueryInterface(DevicePointer, D3D11CaptureInterop.IidDxgiDevice);
        try
        {
            var inspectable = D3D11CaptureInterop.CreateDirect3DDevice(dxgiDevice);
            try
            {
                Device = MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException("Failed to project the WinRT IDirect3DDevice for WGC.", exception);
            }
        }
        finally
        {
            D3D11CaptureInterop.Release(ref dxgiDevice);
        }
    }

    public IDirect3DDevice Device { get; }

    public IntPtr DevicePointer { get; }

    public IntPtr ContextPointer { get; }

    public void Dispose()
    {
        var context = ContextPointer;
        D3D11CaptureInterop.Release(ref context);

        var device = DevicePointer;
        D3D11CaptureInterop.Release(ref device);
    }
}

internal static unsafe class D3D11CaptureInterop
{
    public const uint D3D11SdkVersion = 7;
    public const uint D3D11CreateDeviceBgraSupport = 0x20;

    public static readonly Guid IidDxgiDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
    public static readonly Guid IidD3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
    private static readonly IntPtr D3D11Module = NativeLibrary.Load("d3d11.dll");
    private static readonly D3D11CreateDeviceDelegate D3D11CreateDeviceImpl =
        Marshal.GetDelegateForFunctionPointer<D3D11CreateDeviceDelegate>(NativeLibrary.GetExport(D3D11Module, "D3D11CreateDevice"));
    private static readonly CreateDirect3D11DeviceFromDxgiDeviceDelegate CreateDirect3D11DeviceFromDxgiDeviceImpl =
        Marshal.GetDelegateForFunctionPointer<CreateDirect3D11DeviceFromDxgiDeviceDelegate>(NativeLibrary.GetExport(D3D11Module, "CreateDirect3D11DeviceFromDXGIDevice"));

    public static int D3D11CreateDevice(
        IntPtr adapter,
        D3DDriverType driverType,
        IntPtr software,
        uint flags,
        D3DFeatureLevel* featureLevels,
        uint featureLevelCount,
        uint sdkVersion,
        IntPtr* device,
        D3DFeatureLevel* featureLevel,
        IntPtr* immediateContext)
    {
        return D3D11CreateDeviceImpl(
            adapter,
            driverType,
            software,
            flags,
            featureLevels,
            featureLevelCount,
            sdkVersion,
            device,
            featureLevel,
            immediateContext);
    }

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

    public static IntPtr CreateDirect3DDevice(IntPtr dxgiDevice)
    {
        ThrowIfFailed(CreateDirect3D11DeviceFromDxgiDeviceImpl(dxgiDevice, out var graphicsDevice), "CreateDirect3D11DeviceFromDXGIDevice");
        return graphicsDevice;
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

    public static void CopyResource(IntPtr context, IntPtr destinationResource, IntPtr sourceResource)
    {
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, void>)GetVTableEntry(context, 47))(context, destinationResource, sourceResource);
    }

    public static MappedSubresource Map(IntPtr context, IntPtr resource, uint subresource, MapType mapType, MapFlags mapFlags)
    {
        MappedSubresource mapped;
        var hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, MapType, MapFlags, MappedSubresource*, int>)GetVTableEntry(context, 14))(
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
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, void>)GetVTableEntry(context, 15))(context, resource, subresource);
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

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private unsafe delegate int D3D11CreateDeviceDelegate(
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

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateDirect3D11DeviceFromDxgiDeviceDelegate(IntPtr dxgiDevice, out IntPtr graphicsDevice);
}

internal enum BindFlags : uint
{
    None = 0,
}

internal enum ResourceUsage : uint
{
    Staging = 3,
}

internal enum CpuAccessFlags : uint
{
    Read = 0x20000,
}

internal enum ResourceOptionFlags : uint
{
    None = 0,
}

internal enum DxgiFormat : uint
{
    B8G8R8A8UNorm = 87,
}

internal enum MapType : uint
{
    Read = 1,
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
