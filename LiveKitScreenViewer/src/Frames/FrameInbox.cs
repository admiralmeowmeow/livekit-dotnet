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

    public void Submit(VideoFrame frame)
    {
        if (frame.Source == VideoFrameSource.LiveKit)
        {
            Interlocked.Exchange(ref _latestLiveFrame, frame);
            Interlocked.Exchange(ref _lastLiveFrameTicks, DateTime.UtcNow.Ticks);
            Interlocked.Exchange(ref _hasEverReceivedLiveFrame, 1);
            return;
        }

        Interlocked.Exchange(ref _latestSyntheticFrame, frame);
    }

    public VideoFrame? TakeLatestForRender()
    {
        var latestLiveFrame = Volatile.Read(ref _latestLiveFrame);

        if (HasRecentLiveFrame())
        {
            return latestLiveFrame;
        }

        // Once the real stream has started, keep showing the last live frame through
        // short hiccups instead of visibly bouncing back to the synthetic fallback.
        if (latestLiveFrame is not null && HasEverReceivedLiveFrame() && !HasTimedOutForFallback())
        {
            return latestLiveFrame;
        }

        return Volatile.Read(ref _latestSyntheticFrame);
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
