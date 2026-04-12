using LiveKitD3D11Viewer.Frames;
using LiveKitD3D11Viewer.Rendering;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LiveKitD3D11Viewer.Controls;

public sealed partial class VideoView : UserControl, IDisposable
{
    private readonly VideoFramePool _framePool = new();
    private readonly FrameInbox _frameInbox = new();
    private DxDeviceManager? _deviceManager;
    private SwapChainPanelHost? _panelHost;
    private VideoRenderer? _renderer;
    private RenderLoop? _renderLoop;
    private bool _disposed;

    public VideoFramePool FramePool => _framePool;

    public VideoView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;
    }

    public void SubmitFrame(VideoFrame frame)
    {
        if (_disposed)
        {
            frame.Dispose();
            return;
        }

        _frameInbox.Submit(frame);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _renderLoop?.Dispose();
        _renderer?.Dispose();
        _panelHost?.Dispose();
        _deviceManager?.Dispose();
        _frameInbox.Clear();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_deviceManager is not null)
        {
            return;
        }

        _deviceManager = new DxDeviceManager();
        _panelHost = new SwapChainPanelHost(SwapChainSurface, _deviceManager);
        _renderer = new VideoRenderer(_deviceManager, _panelHost);
        _renderLoop = new RenderLoop(DispatcherQueue, _frameInbox, RenderFrame);
        _renderLoop.Start();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        Dispose();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        _panelHost?.InvalidatePanelMetrics();
    }

    private void RenderFrame(VideoFrame? latestFrame)
    {
        if (_renderer is null)
        {
            return;
        }

        _renderer.Render(latestFrame);
    }

}
