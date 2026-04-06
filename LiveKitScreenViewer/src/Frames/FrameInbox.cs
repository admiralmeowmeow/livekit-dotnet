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
            var replacedLiveFrame = Interlocked.Exchange(ref _latestLiveFrame, frame);
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
        var latestLiveFrame = RetainForRender(Volatile.Read(ref _latestLiveFrame));

        if (HasRecentLiveFrame())
        {
            return latestLiveFrame;
        }

        if (latestLiveFrame is not null && HasEverReceivedLiveFrame() && !HasTimedOutForFallback())
        {
            return latestLiveFrame;
        }

        latestLiveFrame?.Dispose();
        return RetainForRender(Volatile.Read(ref _latestSyntheticFrame));
    }

    public bool NeedsSyntheticFrame()
    {
        var latestLiveFrame = Volatile.Read(ref _latestLiveFrame);
        if (latestLiveFrame is null)
        {
            return true;
        }

        return !HasRecentLiveFrame() && HasTimedOutForFallback();
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

    private static VideoFrame? RetainForRender(VideoFrame? frame)
    {
        return frame is not null && frame.TryAddReference()
            ? frame
            : null;
    }
}
