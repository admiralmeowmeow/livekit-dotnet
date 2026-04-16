using LiveKitScreenViewer.Controls;
using LiveKitScreenViewer.Frames;
using LiveKitScreenViewer.LiveKit;
using LiveKitScreenViewer.Auth;
using Microsoft.UI.Xaml;

namespace LiveKitScreenViewer;

public sealed partial class MainWindow : Window
{
    private readonly DispatcherTimer _frameTimer;
    private readonly DispatcherTimer _statsTimer;
    private readonly RgbaTestPatternGenerator _generator;
    private readonly AppOptions _options = AppOptions.FromEnvironment();
    private readonly LiveKitFrameBridge _liveKitFrameBridge;
    private LiveKitFfiClient? _ffiClient;
    private LiveKitSubscriber? _subscriber;
    private CancellationTokenSource? _subscriberCts;
    private long _frameIndex;
    private bool _receiverStarted;
    private bool _isClosing;

    public MainWindow()
    {
        InitializeComponent();

        _generator = new RgbaTestPatternGenerator(ViewerSurface.FramePool);
        _liveKitFrameBridge = new LiveKitFrameBridge(ViewerSurface);

        _frameTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(1000.0 / 60.0),
        };
        _frameTimer.Tick += OnFrameTick;

        _statsTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250),
        };
        _statsTimer.Tick += OnStatsTick;

        Activated += OnActivated;
        Closed += OnClosed;
        ViewerSurface.RendererStateChanged += OnRendererStateChanged;
    }

    public LiveKitFrameBridge LiveKitFrameBridge => _liveKitFrameBridge;

    private async void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (!_frameTimer.IsEnabled)
        {
            _frameTimer.Start();
            _statsTimer.Start();
            RenderStatusText.Text = "Rendering synthetic fallback feed until LiveKit frames arrive";
            BackendText.Text = $"Backend: {ViewerSurface.CurrentBackendLabel}";
            LiveKitStatusText.Text = $"LiveKit: {_options.Url} / room '{_options.RoomName}'";
            UpdateRenderStats();
        }

        if (_receiverStarted)
        {
            return;
        }

        _receiverStarted = true;

        try
        {
            _ffiClient = new LiveKitFfiClient(_options.ResolveFfiLibraryCandidates());
            _subscriber = new LiveKitSubscriber(_ffiClient, _options, _liveKitFrameBridge);
            _subscriber.StatusChanged += OnSubscriberStatusChanged;
            _subscriberCts = new CancellationTokenSource();
            var token = LiveKitTokenFactory.CreateViewerToken(_options);
            await _subscriber.StartAsync(token, _subscriberCts.Token);
            RenderStatusText.Text = "Connected to LiveKit room. Waiting for subscribed screen frames.";
        }
        catch (Exception ex)
        {
            RenderStatusText.Text = $"LiveKit connect failed, staying on fallback feed: {ex.Message}";
            LiveKitStatusText.Text = $"LiveKit error: {ex.Message}";
        }
    }

    private async void OnClosed(object sender, WindowEventArgs args)
    {
        _isClosing = true;
        Activated -= OnActivated;
        Closed -= OnClosed;
        ViewerSurface.RendererStateChanged -= OnRendererStateChanged;
        _frameTimer.Stop();
        _statsTimer.Stop();
        _frameTimer.Tick -= OnFrameTick;
        _statsTimer.Tick -= OnStatsTick;
        _subscriberCts?.Cancel();

        try
        {
            if (_subscriber is not null)
            {
                _subscriber.StatusChanged -= OnSubscriberStatusChanged;
                await _subscriber.DisposeAsync();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }

        _subscriberCts?.Dispose();
        _ffiClient?.Dispose();
        ViewerSurface.Dispose();
    }

    private void OnRendererStateChanged(object? sender, RendererStateChangedEventArgs e)
    {
        BackendText.Text = $"Backend: {e.BackendLabel}";
        RenderStatusText.Text = e.StatusText;
        UpdateRenderStats();
    }

    private void OnSubscriberStatusChanged(object? sender, string status)
    {
        if (_isClosing)
        {
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            if (_isClosing)
            {
                return;
            }

            LiveKitStatusText.Text = $"LiveKit: {status}";
        });
    }

    private void OnFrameTick(object? sender, object e)
    {
        if (_isClosing || !ViewerSurface.NeedsSyntheticFrame)
        {
            return;
        }

        var frame = _generator.CreateFrame(_frameIndex++, width: 3840, height: 2160);
        ViewerSurface.SubmitFrame(frame);
    }

    private void OnStatsTick(object? sender, object e)
    {
        if (_isClosing)
        {
            return;
        }

        UpdateRenderStats();
    }

    private void UpdateRenderStats()
    {
        FpsText.Text = $"Render FPS: {ViewerSurface.CurrentRenderFps:F1}";
        ReceiveFpsText.Text = $"Receive FPS: {_liveKitFrameBridge.CurrentReceiveFps:F1}";
        FrameAgeText.Text = $"Frame Age: {_liveKitFrameBridge.CurrentFrameAgeMilliseconds:F0} ms";
        UploadModeText.Text = $"Upload: {ViewerSurface.CurrentUploadModeLabel}";
        FlowText.Text = ViewerSurface.CurrentFlowSummary;
        CadenceText.Text = _liveKitFrameBridge.CurrentReceiveCadenceSummary;
        LatencyText.Text = ViewerSurface.CurrentLatencySummary;
        FeedStatusText.Text = $"Content: {ViewerSurface.CurrentContentLabel} | {ViewerSurface.CurrentContentScaleLabel} enabled";
    }
}
