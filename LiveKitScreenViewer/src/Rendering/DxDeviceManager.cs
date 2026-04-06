using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace LiveKitScreenViewer.Rendering;

public sealed class DxDeviceManager : IDisposable
{
    public DxDeviceManager()
    {
        var featureLevels = new[]
        {
            FeatureLevel.Level_11_1,
            FeatureLevel.Level_11_0,
            FeatureLevel.Level_10_1,
        };

        try
        {
            D3D11.D3D11CreateDevice(
                adapter: null,
                driverType: DriverType.Hardware,
                flags: DeviceCreationFlags.BgraSupport,
                featureLevels: featureLevels,
                device: out var hardwareDevice,
                immediateContext: out var hardwareContext).CheckError();

            Device = hardwareDevice;
            Context = hardwareContext;
            IsWarpFallback = false;
        }
        catch
        {
            D3D11.D3D11CreateDevice(
                adapter: null,
                driverType: DriverType.Warp,
                flags: DeviceCreationFlags.BgraSupport,
                featureLevels: featureLevels,
                device: out var warpDevice,
                immediateContext: out var warpContext).CheckError();

            Device = warpDevice;
            Context = warpContext;
            IsWarpFallback = true;
        }

        using var dxgiDevice = Device.QueryInterface<IDXGIDevice1>();
        using var adapter = dxgiDevice.GetAdapter();
        Factory = adapter.GetParent<IDXGIFactory2>();
    }

    public ID3D11Device Device { get; }

    public ID3D11DeviceContext Context { get; }

    public IDXGIFactory2 Factory { get; }

    public bool IsWarpFallback { get; }

    public void Dispose()
    {
        Factory.Dispose();
        Context.Dispose();
        Device.Dispose();
    }
}
