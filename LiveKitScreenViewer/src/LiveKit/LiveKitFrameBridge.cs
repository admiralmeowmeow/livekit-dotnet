using System;
using System.Runtime.InteropServices;
using LiveKitScreenViewer.Controls;
using LiveKitScreenViewer.Frames;

namespace LiveKitScreenViewer.LiveKit;

public sealed class LiveKitFrameBridge
{
    private readonly VideoView _videoView;
    private readonly object _statsLock = new();
    private DateTime _lastReceiveUtc = DateTime.UtcNow;
    private DateTime _lastReceiveSampleUtc = DateTime.UtcNow;
    private double _receiveFramesPerSecond;
    private int _receivedFramesSinceSample;
    private long _lastReceivedFrameIndex;

    public LiveKitFrameBridge(VideoView videoView)
    {
        _videoView = videoView;
    }

    public double CurrentReceiveFps
    {
        get
        {
            lock (_statsLock)
            {
                return _receiveFramesPerSecond;
            }
        }
    }

    public double CurrentFrameAgeMilliseconds
    {
        get
        {
            lock (_statsLock)
            {
                return (DateTime.UtcNow - _lastReceiveUtc).TotalMilliseconds;
            }
        }
    }

    public long LastReceivedFrameIndex
    {
        get
        {
            lock (_statsLock)
            {
                return _lastReceivedFrameIndex;
            }
        }
    }

    public void SubmitRgbaFrame(IntPtr sourceData, int byteLength, int width, int height, int stride, long frameIndex)
    {
        UpdateStats(frameIndex);

        var frame = _videoView.FramePool.Rent(byteLength, width, height, stride, frameIndex, VideoFrameSource.LiveKit);
        unsafe
        {
            fixed (byte* destination = frame.Data)
            {
                System.Buffer.MemoryCopy((void*)sourceData, destination, frame.Data.Length, byteLength);
            }
        }

        _videoView.SubmitFrame(frame);
    }

    private void UpdateStats(long frameIndex)
    {
        lock (_statsLock)
        {
            _lastReceivedFrameIndex = frameIndex;
            _lastReceiveUtc = DateTime.UtcNow;
            _receivedFramesSinceSample++;

            var elapsed = _lastReceiveUtc - _lastReceiveSampleUtc;
            if (elapsed.TotalMilliseconds >= 250)
            {
                _receiveFramesPerSecond = _receivedFramesSinceSample / elapsed.TotalSeconds;
                _receivedFramesSinceSample = 0;
                _lastReceiveSampleUtc = _lastReceiveUtc;
            }
        }
    }
}
