using LiveKitScreenViewer.Frames;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;

namespace LiveKitScreenViewer.Rendering;

public sealed class RenderLoop : IDisposable
{
    private readonly FrameInbox _frameInbox;
    private readonly Action<VideoFrame?> _renderAction;
    private bool _isStarted;

    public RenderLoop(DispatcherQueue dispatcherQueue, FrameInbox frameInbox, Action<VideoFrame?> renderAction)
    {
        _frameInbox = frameInbox;
        _renderAction = renderAction;
    }

    public void Start()
    {
        if (_isStarted)
        {
            return;
        }

        CompositionTarget.Rendering += OnRendering;
        _isStarted = true;
    }

    public void Dispose()
    {
        if (!_isStarted)
        {
            return;
        }

        CompositionTarget.Rendering -= OnRendering;
        _isStarted = false;
    }

    private void OnRendering(object? sender, object args)
    {
        var latestFrame = _frameInbox.TakeLatestForRender();
        try
        {
            _renderAction(latestFrame);
        }
        finally
        {
            latestFrame?.Dispose();
        }
    }
}
