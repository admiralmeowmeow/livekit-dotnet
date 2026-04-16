using LiveKitScreenViewer.Diagnostics;

namespace LiveKitScreenViewer.Frames;

public sealed class VideoFrameTimings
{
    public long FrameReceivedTick { get; private set; }

    public long CpuCopyFinishedTick { get; private set; }

    public long FrameEnqueuedTick { get; private set; }

    public long RenderStartTick { get; private set; }

    public long UploadStartTick { get; private set; }

    public long UploadEndTick { get; private set; }

    public long DrawStartTick { get; private set; }

    public long PresentEndTick { get; private set; }

    public void MarkFrameReceived()
    {
        FrameReceivedTick = StopwatchTickHelpers.GetTimestamp();
    }

    public void MarkCpuCopyFinished()
    {
        CpuCopyFinishedTick = StopwatchTickHelpers.GetTimestamp();
    }

    public void MarkFrameEnqueued()
    {
        FrameEnqueuedTick = StopwatchTickHelpers.GetTimestamp();
    }

    public void MarkRenderStart()
    {
        RenderStartTick = StopwatchTickHelpers.GetTimestamp();
    }

    public void MarkUploadStart()
    {
        UploadStartTick = StopwatchTickHelpers.GetTimestamp();
    }

    public void MarkUploadEnd()
    {
        UploadEndTick = StopwatchTickHelpers.GetTimestamp();
    }

    public void MarkDrawStart()
    {
        DrawStartTick = StopwatchTickHelpers.GetTimestamp();
    }

    public void MarkPresentEnd()
    {
        PresentEndTick = StopwatchTickHelpers.GetTimestamp();
    }

    public void Reset()
    {
        FrameReceivedTick = 0;
        CpuCopyFinishedTick = 0;
        FrameEnqueuedTick = 0;
        RenderStartTick = 0;
        UploadStartTick = 0;
        UploadEndTick = 0;
        DrawStartTick = 0;
        PresentEndTick = 0;
    }
}
