using LiveKitScreenViewer.Frames;

namespace LiveKitScreenViewer.Rendering;

public sealed class RenderWorker : IDisposable
{
    private readonly FrameInbox _frameInbox;
    private readonly Action<VideoFrame> _renderAction;
    private readonly AutoResetEvent _frameSignal = new(false);
    private readonly CancellationTokenSource _disposeCts = new();
    private Thread? _workerThread;

    public RenderWorker(FrameInbox frameInbox, Action<VideoFrame> renderAction)
    {
        _frameInbox = frameInbox;
        _renderAction = renderAction;
    }

    public void Start()
    {
        if (_workerThread is not null)
        {
            return;
        }

        _workerThread = new Thread(Run)
        {
            IsBackground = true,
            Name = "LiveKitScreenViewer.RenderWorker",
            Priority = ThreadPriority.AboveNormal,
        };
        _workerThread.Start();
    }

    public void Dispose()
    {
        _disposeCts.Cancel();
        _frameSignal.Set();

        if (_workerThread is not null)
        {
            _workerThread.Join();
        }

        _frameSignal.Dispose();
        _disposeCts.Dispose();
    }

    public void NotifyFrameAvailable()
    {
        _frameSignal.Set();
    }

    private void Run()
    {
        var handles = new WaitHandle[] { _frameSignal, _disposeCts.Token.WaitHandle };
        while (!_disposeCts.IsCancellationRequested)
        {
            var signaled = WaitHandle.WaitAny(handles);
            if (signaled == 1 || _disposeCts.IsCancellationRequested)
            {
                break;
            }

            while (!_disposeCts.IsCancellationRequested)
            {
                var latestFrame = _frameInbox.TakeLatestForRender();
                if (latestFrame is null)
                {
                    break;
                }

                try
                {
                    _renderAction(latestFrame);
                }
                finally
                {
                    latestFrame.Dispose();
                }
            }
        }
    }
}
