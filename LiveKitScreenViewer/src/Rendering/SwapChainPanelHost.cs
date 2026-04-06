using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Controls;
using Vortice.Direct3D11;
using Vortice.DXGI;
using WinRT;

namespace LiveKitScreenViewer.Rendering;

public sealed class SwapChainPanelHost : IDisposable
{
    [ComImport]
    [Guid("63AAD0B8-7C24-40FF-85A8-640D944CC325")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISwapChainPanelNative
    {
        [PreserveSig]
        int SetSwapChain(IntPtr swapChain);
    }

    private readonly SwapChainPanel _panel;
    private readonly DxDeviceManager _deviceManager;
    private IDXGISwapChain1? _swapChain;
    private ID3D11RenderTargetView? _renderTargetView;
    private uint _bufferWidth;
    private uint _bufferHeight;

    public SwapChainPanelHost(SwapChainPanel panel, DxDeviceManager deviceManager)
    {
        _panel = panel;
        _deviceManager = deviceManager;
    }

    public IDXGISwapChain1 SwapChain => _swapChain ?? throw new InvalidOperationException("Swap chain has not been initialized.");

    public ID3D11RenderTargetView RenderTargetView => _renderTargetView ?? throw new InvalidOperationException("Render target view has not been initialized.");

    public uint BufferWidth => _bufferWidth;

    public uint BufferHeight => _bufferHeight;

    public void InvalidatePanelMetrics()
    {
        // The renderer queries current panel metrics before every present.
    }

    public void EnsureSwapChain(uint preferredWidth, uint preferredHeight)
    {
        var (width, height) = GetPanelPixelSize(preferredWidth, preferredHeight);

        if (_swapChain is null)
        {
            var description = new SwapChainDescription1
            {
                Width = width,
                Height = height,
                Format = Format.R8G8B8A8_UNorm,
                Stereo = false,
                SampleDescription = new SampleDescription(1, 0),
                BufferUsage = Usage.RenderTargetOutput,
                BufferCount = 2,
                Scaling = Scaling.Stretch,
                SwapEffect = SwapEffect.FlipSequential,
                AlphaMode = AlphaMode.Ignore,
                Flags = SwapChainFlags.None,
            };

            _swapChain = _deviceManager.Factory.CreateSwapChainForComposition(_deviceManager.Device, description);
            AttachToPanel(_swapChain);
            _bufferWidth = width;
            _bufferHeight = height;
            CreateRenderTargetView();
            return;
        }

        if (_bufferWidth != width || _bufferHeight != height)
        {
            _renderTargetView?.Dispose();
            _renderTargetView = null;
            _swapChain.ResizeBuffers(2, width, height, Format.R8G8B8A8_UNorm, SwapChainFlags.None).CheckError();
            _bufferWidth = width;
            _bufferHeight = height;
            CreateRenderTargetView();
        }
    }

    public void Present()
    {
        _swapChain?.Present(1, PresentFlags.None);
    }

    public void Dispose()
    {
        _renderTargetView?.Dispose();
        _swapChain?.Dispose();
    }

    private (uint Width, uint Height) GetPanelPixelSize(uint fallbackWidth, uint fallbackHeight)
    {
        var scale = _panel.XamlRoot?.RasterizationScale ?? 1.0;
        var width = (uint)Math.Max(1, (int)Math.Round(_panel.ActualWidth * scale));
        var height = (uint)Math.Max(1, (int)Math.Round(_panel.ActualHeight * scale));

        if (_panel.ActualWidth <= 0 || _panel.ActualHeight <= 0)
        {
            width = Math.Max(1u, fallbackWidth);
            height = Math.Max(1u, fallbackHeight);
        }

        return (width, height);
    }

    private void CreateRenderTargetView()
    {
        using var backBuffer = _swapChain!.GetBuffer<ID3D11Texture2D>(0);
        _renderTargetView = _deviceManager.Device.CreateRenderTargetView(backBuffer);
    }

    private void AttachToPanel(IDXGISwapChain1 swapChain)
    {
        var winrtObject = (IWinRTObject)_panel;
        IntPtr unknownPointer = winrtObject.NativeObject.ThisPtr;
        var guid = typeof(ISwapChainPanelNative).GUID;
        var hr = Marshal.QueryInterface(unknownPointer, ref guid, out var panelPointer);
        if (hr != 0)
        {
            throw new InvalidOperationException($"QueryInterface(ISwapChainPanelNative) failed with hr=0x{hr:X8}.");
        }

        try
        {
            var nativePanel = (ISwapChainPanelNative)Marshal.GetObjectForIUnknown(panelPointer);
            nativePanel.SetSwapChain(swapChain.NativePointer);
        }
        finally
        {
            Marshal.Release(panelPointer);
        }
    }
}
