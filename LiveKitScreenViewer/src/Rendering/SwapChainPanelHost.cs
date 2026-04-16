using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
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
    private readonly DispatcherQueue _dispatcherQueue;
    private IntPtr _swapChain;
    private IntPtr _renderTargetView;
    private uint _bufferWidth;
    private uint _bufferHeight;
    private uint _panelPixelWidth;
    private uint _panelPixelHeight;
    private const uint PreferredBufferCount = 2;

    public SwapChainPanelHost(SwapChainPanel panel, DxDeviceManager deviceManager)
    {
        _panel = panel;
        _deviceManager = deviceManager;
        _dispatcherQueue = panel.DispatcherQueue;
        UpdatePanelMetrics();
    }

    public IntPtr SwapChain => _swapChain != IntPtr.Zero ? _swapChain : throw new InvalidOperationException("Swap chain has not been initialized.");

    public IntPtr RenderTargetView => _renderTargetView != IntPtr.Zero ? _renderTargetView : throw new InvalidOperationException("Render target view has not been initialized.");

    public uint BufferWidth => _bufferWidth;

    public uint BufferHeight => _bufferHeight;

    public void InvalidatePanelMetrics()
    {
        UpdatePanelMetrics();
    }

    public void EnsureSwapChain(uint preferredWidth, uint preferredHeight)
    {
        var (width, height) = GetPanelPixelSize(preferredWidth, preferredHeight);

        if (_swapChain == IntPtr.Zero)
        {
            var description = new SwapChainDescription1
            {
                Width = width,
                Height = height,
                Format = DxgiFormat.R8G8B8A8UNorm,
                Stereo = false,
                SampleDescription = new SampleDescription(1, 0),
                BufferUsage = Direct3D11Interop.DxgiUsageRenderTargetOutput,
                BufferCount = PreferredBufferCount,
                Scaling = Scaling.Stretch,
                SwapEffect = SwapEffect.FlipSequential,
                AlphaMode = AlphaMode.Ignore,
                Flags = 0,
            };

            _swapChain = Direct3D11Interop.CreateSwapChainForComposition(_deviceManager.Factory, _deviceManager.Device, description);
            AttachToPanelOnUiThread(_swapChain);
            _bufferWidth = width;
            _bufferHeight = height;
            CreateRenderTargetView();
            return;
        }

        if (_bufferWidth != width || _bufferHeight != height)
        {
            Direct3D11Interop.Release(ref _renderTargetView);
            Direct3D11Interop.ResizeBuffers(_swapChain, PreferredBufferCount, width, height, DxgiFormat.R8G8B8A8UNorm, 0);
            _bufferWidth = width;
            _bufferHeight = height;
            CreateRenderTargetView();
        }
    }

    public void Present()
    {
        if (_swapChain == IntPtr.Zero)
        {
            return;
        }

        var hr = Direct3D11Interop.TryPresent(_swapChain, 0, 0);
        if (hr >= 0)
        {
            return;
        }

        if (hr == DxgiErrorWasStillDrawing)
        {
            hr = Direct3D11Interop.TryPresent(_swapChain, 1, 0);
        }

        Direct3D11Interop.ThrowIfFailed(hr, "IDXGISwapChain::Present");
    }

    public void Dispose()
    {
        Direct3D11Interop.Release(ref _renderTargetView);
        Direct3D11Interop.Release(ref _swapChain);
    }

    private (uint Width, uint Height) GetPanelPixelSize(uint fallbackWidth, uint fallbackHeight)
    {
        var width = Volatile.Read(ref _panelPixelWidth);
        var height = Volatile.Read(ref _panelPixelHeight);
        if (width == 0 || height == 0)
        {
            width = Math.Max(1u, fallbackWidth);
            height = Math.Max(1u, fallbackHeight);
        }

        return (width, height);
    }

    private void CreateRenderTargetView()
    {
        var backBuffer = Direct3D11Interop.GetBuffer(_swapChain, 0, Direct3D11Interop.IidD3D11Texture2D);
        try
        {
            _renderTargetView = Direct3D11Interop.CreateRenderTargetView(_deviceManager.Device, backBuffer);
        }
        finally
        {
            Direct3D11Interop.Release(ref backBuffer);
        }
    }

    private void UpdatePanelMetrics()
    {
        var width = (uint)Math.Max(1, (int)Math.Round(_panel.ActualWidth));
        var height = (uint)Math.Max(1, (int)Math.Round(_panel.ActualHeight));
        Volatile.Write(ref _panelPixelWidth, width);
        Volatile.Write(ref _panelPixelHeight, height);
    }

    private void AttachToPanelOnUiThread(IntPtr swapChain)
    {
        if (_dispatcherQueue.HasThreadAccess)
        {
            AttachToPanel(swapChain);
            return;
        }

        using var attached = new ManualResetEventSlim();
        Exception? dispatchException = null;
        if (!_dispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                AttachToPanel(swapChain);
            }
            catch (Exception ex)
            {
                dispatchException = ex;
            }
            finally
            {
                attached.Set();
            }
        }))
        {
            throw new InvalidOperationException("Failed to enqueue SwapChainPanel association on the UI thread.");
        }

        attached.Wait();
        if (dispatchException is not null)
        {
            throw dispatchException;
        }
    }

    private void AttachToPanel(IntPtr swapChain)
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
            nativePanel.SetSwapChain(swapChain);
        }
        finally
        {
            Marshal.Release(panelPointer);
        }
    }

    private const int DxgiErrorWasStillDrawing = unchecked((int)0x887A000A);
}
