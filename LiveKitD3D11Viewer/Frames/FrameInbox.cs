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
        return Interlocked.Exchange(ref _latestFrame, null);
    }

    public void Clear()
    {
        var frame = Interlocked.Exchange(ref _latestFrame, null);
        frame?.Dispose();
    }
}
