using System;
using System.Runtime.InteropServices;
using LiveKitScreenViewer.Controls;
using LiveKitScreenViewer.Diagnostics;
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
    private long _receivedFrameCount;
    private long _droppedSourceFrames;
    private readonly RollingMetric _receiveIntervalMilliseconds = new(512);

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

    public long ReceivedFrameCount
    {
        get
        {
            lock (_statsLock)
            {
                return _receivedFrameCount;
            }
        }
    }

    public long DroppedSourceFrames
    {
        get
        {
            lock (_statsLock)
            {
                return _droppedSourceFrames;
            }
        }
    }

    public string CurrentReceiveCadenceSummary
    {
        get
        {
            lock (_statsLock)
            {
                var snapshot = _receiveIntervalMilliseconds.GetSnapshot();
                if (snapshot.Count == 0)
                {
                    return "Cadence: waiting for live frames";
                }

                return $"Cadence: avg {snapshot.Average:F1} ms, p95 {snapshot.P95:F1} ms, max {snapshot.Max:F1} ms, source gaps {_droppedSourceFrames}";
            }
        }
    }

    public void SubmitRgbaFrame(IntPtr sourceData, int byteLength, int width, int height, int stride, long frameIndex)
    {
        UpdateStats(frameIndex);

        var frame = _videoView.FramePool.Rent(byteLength, width, height, stride, frameIndex, VideoFrameSource.LiveKit);
        frame.Timings.MarkFrameReceived();
        unsafe
        {
            System.Buffer.MemoryCopy((void*)sourceData, (void*)frame.DataPointer, frame.ByteLength, byteLength);
        }
        frame.Timings.MarkCpuCopyFinished();

        _videoView.SubmitFrame(frame);
    }

    private void UpdateStats(long frameIndex)
    {
        lock (_statsLock)
        {
            var previousReceiveUtc = _lastReceiveUtc;
            var previousFrameIndex = _lastReceivedFrameIndex;
            _lastReceivedFrameIndex = frameIndex;
            _lastReceiveUtc = DateTime.UtcNow;
            _receivedFrameCount++;
            _receivedFramesSinceSample++;

            if (_receivedFrameCount > 1)
            {
                _receiveIntervalMilliseconds.Add((_lastReceiveUtc - previousReceiveUtc).TotalMilliseconds);
                if (frameIndex > previousFrameIndex + 1)
                {
                    _droppedSourceFrames += frameIndex - previousFrameIndex - 1;
                }
            }

            var elapsed = _lastReceiveUtc - _lastReceiveSampleUtc;
            if (elapsed.TotalMilliseconds >= 250)
            {
                _receiveFramesPerSecond = _receivedFramesSinceSample / elapsed.TotalSeconds;
                _receivedFramesSinceSample = 0;
                _lastReceiveSampleUtc = _lastReceiveUtc;
            }
        }
    }

    private sealed class RollingMetric
    {
        private readonly double[] _samples;
        private int _count;
        private int _nextIndex;

        public RollingMetric(int capacity)
        {
            _samples = new double[capacity];
        }

        public void Add(double value)
        {
            _samples[_nextIndex] = value;
            _nextIndex = (_nextIndex + 1) % _samples.Length;
            if (_count < _samples.Length)
            {
                _count++;
            }
        }

        public MetricSnapshot GetSnapshot()
        {
            if (_count == 0)
            {
                return default;
            }

            var values = new double[_count];
            Array.Copy(_samples, values, _count);
            Array.Sort(values);

            double sum = 0;
            for (var i = 0; i < values.Length; i++)
            {
                sum += values[i];
            }

            return new MetricSnapshot(
                values.Length,
                sum / values.Length,
                Percentile(values, 0.95),
                values[^1]);
        }

        private static double Percentile(double[] sortedValues, double percentile)
        {
            if (sortedValues.Length == 1)
            {
                return sortedValues[0];
            }

            var index = (int)Math.Ceiling((sortedValues.Length - 1) * percentile);
            return sortedValues[Math.Clamp(index, 0, sortedValues.Length - 1)];
        }
    }

    private readonly record struct MetricSnapshot(int Count, double Average, double P95, double Max);
}
