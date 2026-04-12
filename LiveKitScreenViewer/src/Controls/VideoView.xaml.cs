using LiveKitScreenViewer.Frames;
using LiveKitScreenViewer.Rendering;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LiveKitScreenViewer.Controls;

public sealed partial class VideoView : UserControl, IDisposable
{
    private readonly VideoFramePool _framePool = new();
    private readonly FrameInbox _frameInbox = new();
    private DxDeviceManager? _deviceManager;
    private SwapChainPanelHost? _panelHost;
    private VideoRenderer? _renderer;
    private RenderLoop? _renderLoop;
    private bool _disposed;
    private VideoFrameSource? _lastRenderedSource;
    private int _currentFrameWidth;
    private int _currentFrameHeight;

    public event EventHandler<RendererStateChangedEventArgs>? RendererStateChanged;

    public string CurrentBackendLabel => _deviceManager is null
        ? "initializing"
        : _deviceManager.IsWarpFallback ? "D3D11 WARP" : "D3D11 hardware";

    public double CurrentRenderFps => _renderer?.CurrentFramesPerSecond ?? 0;

    public string CurrentContentScaleLabel => _renderer?.ContentScaleLabel ?? "aspect fill";

    public VideoFramePool FramePool => _framePool;

    public bool NeedsSyntheticFrame => _frameInbox.NeedsSyntheticFrame();

    public string CurrentContentLabel => _currentFrameWidth <= 0 || _currentFrameHeight <= 0
        ? "No frames yet"
        : $"{_currentFrameWidth}x{_currentFrameHeight}";

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

        RaiseRendererStateChanged("Rendering synthetic fallback frames");
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

        if (latestFrame is not null)
        {
            _currentFrameWidth = latestFrame.Width;
            _currentFrameHeight = latestFrame.Height;
        }

        _renderer.Render(latestFrame);

        if (latestFrame is not null)
        {
            UpdateOverlay(latestFrame);
        }
    }

    private void UpdateOverlay(VideoFrame frame)
    {
        IdleOverlay.Visibility = Visibility.Collapsed;
        LiveOverlay.Visibility = frame.Source == VideoFrameSource.LiveKit
            ? Visibility.Collapsed
            : Visibility.Visible;

        if (_lastRenderedSource != frame.Source)
        {
            _lastRenderedSource = frame.Source;
            var statusText = frame.Source == VideoFrameSource.LiveKit
                ? "Rendering LiveKit RGBA feed"
                : "Rendering synthetic fallback feed";
            RaiseRendererStateChanged(statusText);
        }

        if (frame.Source == VideoFrameSource.LiveKit)
        {
            OverlayTitleText.Text = "Receiving LiveKit RGBA frames";
            OverlayDetailText.Text = $"Live frame: {frame.Width}x{frame.Height} | render {CurrentRenderFps:F1} fps | frame #{frame.FrameIndex}";
            return;
        }

        OverlayTitleText.Text = "Rendering fallback RGBA feed";
        OverlayDetailText.Text = $"Fallback frame: {frame.Width}x{frame.Height} | render {CurrentRenderFps:F1} fps | frame #{frame.FrameIndex}";
    }

    private void RaiseRendererStateChanged(string statusText)
    {
        RendererStateChanged?.Invoke(this, new RendererStateChangedEventArgs(CurrentBackendLabel, statusText));
    }

}

public sealed class RendererStateChangedEventArgs : EventArgs
{
    public RendererStateChangedEventArgs(string backendLabel, string statusText)
    {
        BackendLabel = backendLabel;
        StatusText = statusText;
    }

    public string BackendLabel { get; }

    public string StatusText { get; }
}
