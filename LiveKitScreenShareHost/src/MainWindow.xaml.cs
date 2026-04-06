using LiveKitScreenShareHost.Auth;
using LiveKitScreenShareHost.Capture;
using LiveKitScreenShareHost.Hosting;
using LiveKitScreenShareHost.LiveKit;
using LiveKitScreenShareHost.Viewer;
using Microsoft.UI.Windowing;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using WinRT.Interop;

namespace LiveKitScreenShareHost;

public sealed partial class MainWindow : Window
{
    private readonly AppOptions _options;
    private readonly List<DisplayOption> _displayOptions;
    private HostedLiveKitServer? _hostedServer;
    private LocalViewerHost? _viewerHost;
    private LiveKitFfiClient? _ffiClient;
    private LiveKitPublisher? _publisher;
    private PrimaryScreenCapturer? _capturer;
    private CancellationTokenSource? _streamCancellation;
    private Task? _shareLoopTask;
    private bool _isSharing;

    public MainWindow()
    {
        InitializeComponent();

        Title = "LiveKit Screen Share Host";
        ConfigureStartupWindow();
        Closed += MainWindow_Closed;
        _options = AppOptions.FromEnvironment(Environment.GetCommandLineArgs().Skip(1).ToArray());
        _displayOptions = DisplayCatalog.GetActiveDisplays().ToList();

        DisplayRadioButtons.ItemsSource = _displayOptions;
        DisplayRadioButtons.SelectedItem = _displayOptions.FirstOrDefault(option => option.IsPrimary) ?? _displayOptions.FirstOrDefault();

        ServerUrlText.Text = $"LiveKit server: {_options.Url}" + (_options.HostServer ? " (launched by app)" : " (external server mode)");
        UpdateButtons();
        UpdateSubtitle();
    }

    private DisplayOption? SelectedDisplay => DisplayRadioButtons.SelectedItem as DisplayOption;

    private async void ShareButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isSharing)
        {
            return;
        }

        var display = SelectedDisplay;
        if (display is null)
        {
            await ShowMessageAsync("No display selected", "Choose a display before starting the stream.");
            return;
        }

        StatusText.Text = _options.HostServer
            ? "Starting LiveKit server and opening the stream..."
            : "Connecting to external LiveKit server and opening the stream...";
        ShareButton.IsEnabled = false;
        StopButton.IsEnabled = false;
        DisplayRadioButtons.IsEnabled = false;

        try
        {
            await StartSharingAsync(display);
        }
        catch (Exception ex)
        {
            await StopSharingCoreAsync();
            StatusText.Text = $"Share failed: {ex.Message}";
            await ShowMessageAsync("Share failed", ex.Message);
        }
        finally
        {
            UpdateButtons();
        }
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e)
    {
        await StopSharingCoreAsync();
        UpdateButtons();
    }

    private void DisplayRadioButtons_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateSubtitle();
    }

    private async void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        await StopSharingCoreAsync();
    }

    private async Task StartSharingAsync(DisplayOption display)
    {
        _streamCancellation = new CancellationTokenSource();
        var token = _streamCancellation.Token;

        _hostedServer = await HostedLiveKitServer.StartIfRequestedAsync(_options, token);
        _ffiClient = new LiveKitFfiClient(_options.ResolveFfiLibraryCandidates());
        _capturer = new PrimaryScreenCapturer(display);
        _publisher = new LiveKitPublisher(_ffiClient, _capturer, _options);

        var publisherToken = LiveKitTokenFactory.CreatePublisherToken(_options);
        var viewerToken = LiveKitTokenFactory.CreateViewerToken(_options);
        _viewerHost = new LocalViewerHost(_options, viewerToken);
        await _viewerHost.StartAsync(token);
        await _publisher.StartAsync(publisherToken, token);

        _isSharing = true;
        StatusText.Text = $"Streaming {display.DisplayName}.";
        StreamingInfoText.Text = $"Streaming {display.Description} as RGBA at {_options.CaptureFps} fps.";
        ViewerLinkButton.Content = _viewerHost.ViewerUrl;
        ViewerLinkButton.IsEnabled = true;

        _shareLoopTask = Task.Run(async () =>
        {
            try
            {
                await _publisher.RunUntilCancelledAsync(token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                DispatcherQueue.TryEnqueue(async () =>
                {
                    StatusText.Text = $"Streaming stopped with an error: {ex.Message}";
                    await StopSharingCoreAsync();
                    UpdateButtons();
                });
            }
        }, token);
    }

    private async Task StopSharingCoreAsync()
    {
        if (_streamCancellation is not null && !_streamCancellation.IsCancellationRequested)
        {
            _streamCancellation.Cancel();
        }

        if (_shareLoopTask is not null)
        {
            try
            {
                await _shareLoopTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        if (_publisher is not null)
        {
            await _publisher.DisposeAsync();
            _publisher = null;
        }

        _capturer?.Dispose();
        _capturer = null;

        _ffiClient?.Dispose();
        _ffiClient = null;

        if (_viewerHost is not null)
        {
            await _viewerHost.DisposeAsync();
            _viewerHost = null;
        }

        if (_hostedServer is not null)
        {
            await _hostedServer.DisposeAsync();
            _hostedServer = null;
        }

        _shareLoopTask = null;
        _streamCancellation?.Dispose();
        _streamCancellation = null;
        _isSharing = false;

        StatusText.Text = "Ready. Select a display and press Share.";
        StreamingInfoText.Text = "No active stream.";
        ViewerLinkButton.Content = "Open local viewer";
        ViewerLinkButton.IsEnabled = false;
    }

    private void UpdateButtons()
    {
        ShareButton.IsEnabled = !_isSharing && SelectedDisplay is not null;
        StopButton.IsEnabled = _isSharing;
        DisplayRadioButtons.IsEnabled = !_isSharing;
    }

    private void UpdateSubtitle()
    {
        if (SelectedDisplay is null)
        {
            SubtitleText.Text = "No active display was found. Connect a monitor and reopen the app.";
            return;
        }

        SubtitleText.Text = $"Selected display: {SelectedDisplay.Description}. Streaming uses RGBA source buffers at {_options.CaptureFps} fps.";
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = "OK",
            XamlRoot = (Content as FrameworkElement)?.XamlRoot,
        };

        await dialog.ShowAsync();
    }

    private void ViewerLinkButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewerHost is null)
        {
            return;
        }

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = _viewerHost.ViewerUrl,
            UseShellExecute = true,
        };

        System.Diagnostics.Process.Start(psi);
    }

    private void ConfigureStartupWindow()
    {
        var windowHandle = WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(windowHandle);
        var appWindow = AppWindow.GetFromWindowId(windowId);
        appWindow.Resize(new SizeInt32(920, 680));
    }
}
