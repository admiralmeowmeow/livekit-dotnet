namespace LiveKitD3D11Viewer.Rendering;

public sealed class DxDeviceManager : IDisposable
{
    public DxDeviceManager()
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

                var hr = Direct3D11Interop.D3D11CreateDevice(
                    IntPtr.Zero,
                    D3DDriverType.Hardware,
                    IntPtr.Zero,
                    Direct3D11Interop.D3D11CreateDeviceBgraSupport,
                    requestedFeatureLevels,
                    (uint)featureLevels.Length,
                    Direct3D11Interop.D3D11SdkVersion,
                    &device,
                    &actualFeatureLevel,
                    &context);

                if (hr < 0)
                {
                    Direct3D11Interop.ThrowIfFailed(
                        Direct3D11Interop.D3D11CreateDevice(
                            IntPtr.Zero,
                            D3DDriverType.Warp,
                            IntPtr.Zero,
                            Direct3D11Interop.D3D11CreateDeviceBgraSupport,
                            requestedFeatureLevels,
                            (uint)featureLevels.Length,
                            Direct3D11Interop.D3D11SdkVersion,
                            &device,
                            &actualFeatureLevel,
                            &context),
                        "D3D11CreateDevice");

                    IsWarpFallback = true;
                }

                Device = device;
                Context = context;
                FeatureLevel = actualFeatureLevel;
            }
        }

        var dxgiDevice = Direct3D11Interop.QueryInterface(Device, Direct3D11Interop.IidDxgiDevice1);
        try
        {
            try
            {
                Direct3D11Interop.SetMaximumFrameLatency(dxgiDevice, 1);
            }
            catch
            {
            }

            var adapter = Direct3D11Interop.GetAdapter(dxgiDevice);
            try
            {
                Factory = Direct3D11Interop.GetParent(adapter, Direct3D11Interop.IidDxgiFactory2);
            }
            finally
            {
                Direct3D11Interop.Release(ref adapter);
            }
        }
        finally
        {
            Direct3D11Interop.Release(ref dxgiDevice);
        }
    }

    public IntPtr Device { get; }

    public IntPtr Context { get; }

    public IntPtr Factory { get; }

    public D3DFeatureLevel FeatureLevel { get; }

    public bool IsWarpFallback { get; }

    public void Dispose()
    {
        var factory = Factory;
        Direct3D11Interop.Release(ref factory);

        var context = Context;
        Direct3D11Interop.Release(ref context);

        var device = Device;
        Direct3D11Interop.Release(ref device);
    }
}
