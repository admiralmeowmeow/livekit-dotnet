using System.Threading;

namespace LiveKitScreenViewer.Frames;

public sealed class FrameInbox
{
    private readonly long _liveFrameTimeoutTicks = TimeSpan.FromMilliseconds(350).Ticks;
    private readonly long _fallbackReentryTimeoutTicks = TimeSpan.FromSeconds(5).Ticks;
    private VideoFrame? _latestSyntheticFrame;
    private VideoFrame? _latestLiveFrame;
    private long _lastLiveFrameTicks;
    private int _hasEverReceivedLiveFrame;
    private long _liveFramesSubmitted;
    private long _liveFramesReplacedBeforeRender;

    public void Submit(VideoFrame frame)
    {
        frame.Timings.MarkFrameEnqueued();

        if (frame.Source == VideoFrameSource.LiveKit)
        {
            Interlocked.Increment(ref _liveFramesSubmitted);
            var replacedLiveFrame = Interlocked.Exchange(ref _latestLiveFrame, frame);
            if (replacedLiveFrame is not null)
            {
                Interlocked.Increment(ref _liveFramesReplacedBeforeRender);
            }
            replacedLiveFrame?.Dispose();
            Interlocked.Exchange(ref _lastLiveFrameTicks, DateTime.UtcNow.Ticks);
            Interlocked.Exchange(ref _hasEverReceivedLiveFrame, 1);
            return;
        }

        var replacedSyntheticFrame = Interlocked.Exchange(ref _latestSyntheticFrame, frame);
        replacedSyntheticFrame?.Dispose();
    }

    public VideoFrame? TakeLatestForRender()
    {
        var latestLiveFrame = Interlocked.Exchange(ref _latestLiveFrame, null);

        if (HasRecentLiveFrame())
        {
            return latestLiveFrame;
        }

        if (latestLiveFrame is not null && HasEverReceivedLiveFrame() && !HasTimedOutForFallback())
        {
            latestLiveFrame.Dispose();
            return null;
        }

        latestLiveFrame?.Dispose();
        return Interlocked.Exchange(ref _latestSyntheticFrame, null);
    }

    public bool NeedsSyntheticFrame()
    {
        if (HasRecentLiveFrame())
        {
            return false;
        }

        if (HasEverReceivedLiveFrame() && !HasTimedOutForFallback())
        {
            return false;
        }

        return true;
    }

    public bool HasRecentLiveFrame()
    {
        var lastLiveTicks = Interlocked.Read(ref _lastLiveFrameTicks);
        if (lastLiveTicks == 0)
        {
            return false;
        }

        return DateTime.UtcNow.Ticks - lastLiveTicks <= _liveFrameTimeoutTicks;
    }

    public void Clear()
    {
        var liveFrame = Interlocked.Exchange(ref _latestLiveFrame, null);
        liveFrame?.Dispose();

        var syntheticFrame = Interlocked.Exchange(ref _latestSyntheticFrame, null);
        syntheticFrame?.Dispose();

        Interlocked.Exchange(ref _lastLiveFrameTicks, 0);
        Interlocked.Exchange(ref _hasEverReceivedLiveFrame, 0);
        Interlocked.Exchange(ref _liveFramesSubmitted, 0);
        Interlocked.Exchange(ref _liveFramesReplacedBeforeRender, 0);
    }

    public long LiveFramesSubmitted => Interlocked.Read(ref _liveFramesSubmitted);

    public long LiveFramesReplacedBeforeRender => Interlocked.Read(ref _liveFramesReplacedBeforeRender);

    public string GetLiveFlowSummary(long renderedLiveFrames)
    {
        var submitted = Interlocked.Read(ref _liveFramesSubmitted);
        var replaced = Interlocked.Read(ref _liveFramesReplacedBeforeRender);
        var rendered = Math.Min(renderedLiveFrames, submitted);
        var renderedPercent = submitted == 0 ? 0.0 : rendered * 100.0 / submitted;
        var replacedPercent = submitted == 0 ? 0.0 : replaced * 100.0 / submitted;
        return $"Flow: recv {submitted}, rendered {rendered} ({renderedPercent:F0}%), replaced {replaced} ({replacedPercent:F0}%)";
    }

    private bool HasEverReceivedLiveFrame()
    {
        return Volatile.Read(ref _hasEverReceivedLiveFrame) != 0;
    }

    private bool HasTimedOutForFallback()
    {
        var lastLiveTicks = Interlocked.Read(ref _lastLiveFrameTicks);
        if (lastLiveTicks == 0)
        {
            return true;
        }

        return DateTime.UtcNow.Ticks - lastLiveTicks > _fallbackReentryTimeoutTicks;
    }
}
