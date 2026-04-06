using LiveKitScreenViewer.Frames;
using Microsoft.UI.Dispatching;

namespace LiveKitScreenViewer.Rendering;

public sealed class RenderLoop : IDisposable
{
    private readonly DispatcherQueueTimer _timer;
    private readonly FrameInbox _frameInbox;
    private readonly Action<VideoFrame?> _renderAction;

    public RenderLoop(DispatcherQueue dispatcherQueue, FrameInbox frameInbox, Action<VideoFrame?> renderAction)
    {
        _frameInbox = frameInbox;
        _renderAction = renderAction;
        _timer = dispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(1000.0 / 60.0);
        _timer.Tick += OnTick;
    }

    public void Start()
    {
        if (!_timer.IsRunning)
        {
            _timer.Start();
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Tick -= OnTick;
    }

    private void OnTick(DispatcherQueueTimer sender, object args)
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
