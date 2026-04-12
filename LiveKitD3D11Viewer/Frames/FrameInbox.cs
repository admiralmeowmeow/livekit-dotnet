using System.Threading;

namespace LiveKitD3D11Viewer.Frames;

public sealed class FrameInbox
{
    private VideoFrame? _latestFrame;

    public void Submit(VideoFrame frame)
    {
        var replacedFrame = Interlocked.Exchange(ref _latestFrame, frame);
        replacedFrame?.Dispose();
    }

    public VideoFrame? TakeLatestForRender()
    {
        return RetainForRender(Volatile.Read(ref _latestFrame));
    }

    public void Clear()
    {
        var frame = Interlocked.Exchange(ref _latestFrame, null);
        frame?.Dispose();
    }

    private static VideoFrame? RetainForRender(VideoFrame? frame)
    {
        return frame is not null && frame.TryAddReference()
            ? frame
            : null;
    }
}
