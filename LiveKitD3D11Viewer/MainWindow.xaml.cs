using LiveKitD3D11Viewer.Controls;
using LiveKitD3D11Viewer.LiveKit;
using LiveKitD3D11Viewer.Auth;
using Microsoft.UI.Xaml;

namespace LiveKitD3D11Viewer;

public sealed partial class MainWindow : Window
{
    private readonly AppOptions _options = AppOptions.FromEnvironment();
    private readonly LiveKitFrameBridge _liveKitFrameBridge;
    private LiveKitFfiClient? _ffiClient;
    private LiveKitSubscriber? _subscriber;
    private CancellationTokenSource? _subscriberCts;
    private bool _connectionStarted;
    private bool _isClosing;

    public MainWindow()
    {
        InitializeComponent();

        _liveKitFrameBridge = new LiveKitFrameBridge(ViewerSurface);

        Activated += OnActivated;
        Closed += OnClosed;
    }

    private async void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (_connectionStarted || _isClosing)
        {
            return;
        }

        _connectionStarted = true;

        try
        {
            _ffiClient = new LiveKitFfiClient(_options.ResolveFfiLibraryCandidates());
            _subscriber = new LiveKitSubscriber(_ffiClient, _options, _liveKitFrameBridge);
            _subscriberCts = new CancellationTokenSource();
            var token = LiveKitTokenFactory.CreateViewerToken(_options);
            await _subscriber.StartAsync(token, _subscriberCts.Token);
        }
        catch
        {
            await DisposeLiveKitAsync();
        }
    }

    private async void OnClosed(object sender, WindowEventArgs args)
    {
        _isClosing = true;
        Activated -= OnActivated;
        Closed -= OnClosed;
        await DisposeLiveKitAsync();
        ViewerSurface.Dispose();
    }

    private async Task DisposeLiveKitAsync()
    {
        _subscriberCts?.Cancel();

        try
        {
            if (_subscriber is not null)
            {
                await _subscriber.DisposeAsync();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }

        _subscriber = null;
        _subscriberCts?.Dispose();
        _subscriberCts = null;
        _ffiClient?.Dispose();
        _ffiClient = null;
    }
}
