using System.Diagnostics;
using System.Text;
using LiveKitScreenViewer.Frames;

namespace LiveKitScreenViewer.Diagnostics;

public sealed class FrameLatencyTracker
{
    private readonly object _sync = new();
    private readonly RollingMetric _receiveToCopy = new(512);
    private readonly RollingMetric _copyToEnqueue = new(512);
    private readonly RollingMetric _enqueueToRender = new(512);
    private readonly RollingMetric _renderToUpload = new(512);
    private readonly RollingMetric _uploadDuration = new(512);
    private readonly RollingMetric _uploadToDraw = new(512);
    private readonly RollingMetric _drawToPresent = new(512);
    private readonly RollingMetric _endToEnd = new(512);
    private string _lastLoggedSummary = "Latency: waiting for live frames";
    private long _lastLogTick;

    public string GetSummaryText()
    {
        lock (_sync)
        {
            if (_endToEnd.Count == 0)
            {
                return "Latency: waiting for live frames";
            }

            var builder = new StringBuilder();
            AppendMetric(builder, "end-to-end", _endToEnd.GetSnapshot());
            AppendMetric(builder, "recv->copy", _receiveToCopy.GetSnapshot());
            AppendMetric(builder, "copy->enqueue", _copyToEnqueue.GetSnapshot());
            AppendMetric(builder, "enqueue->render", _enqueueToRender.GetSnapshot());
            AppendMetric(builder, "render->upload", _renderToUpload.GetSnapshot());
            AppendMetric(builder, "upload", _uploadDuration.GetSnapshot());
            AppendMetric(builder, "upload->draw", _uploadToDraw.GetSnapshot());
            AppendMetric(builder, "draw->present", _drawToPresent.GetSnapshot());
            return builder.ToString().TrimEnd();
        }
    }

    public void RecordPresentedFrame(VideoFrame frame)
    {
        if (frame.Source != VideoFrameSource.LiveKit)
        {
            return;
        }

        var timings = frame.Timings;
        if (timings.FrameReceivedTick == 0 || timings.PresentEndTick == 0)
        {
            return;
        }

        lock (_sync)
        {
            AddIfPositive(_receiveToCopy, timings.CpuCopyFinishedTick - timings.FrameReceivedTick);
            AddIfPositive(_copyToEnqueue, timings.FrameEnqueuedTick - timings.CpuCopyFinishedTick);
            AddIfPositive(_enqueueToRender, timings.RenderStartTick - timings.FrameEnqueuedTick);
            AddIfPositive(_renderToUpload, timings.UploadStartTick - timings.RenderStartTick);
            AddIfPositive(_uploadDuration, timings.UploadEndTick - timings.UploadStartTick);
            AddIfPositive(_uploadToDraw, timings.DrawStartTick - timings.UploadEndTick);
            AddIfPositive(_drawToPresent, timings.PresentEndTick - timings.DrawStartTick);
            AddIfPositive(_endToEnd, timings.PresentEndTick - timings.FrameReceivedTick);

            var nowTick = Stopwatch.GetTimestamp();
            if (_lastLogTick == 0 || StopwatchTickHelpers.ToMilliseconds(nowTick - _lastLogTick) >= 2_000)
            {
                _lastLoggedSummary = GetSummaryText();
                _lastLogTick = nowTick;
                Debug.WriteLine($"[LiveKitViewer][Latency]\n{_lastLoggedSummary}");
            }
        }
    }

    private static void AddIfPositive(RollingMetric metric, long elapsedTicks)
    {
        if (elapsedTicks <= 0)
        {
            return;
        }

        metric.Add(StopwatchTickHelpers.ToMilliseconds(elapsedTicks));
    }

    private static void AppendMetric(StringBuilder builder, string label, MetricSnapshot snapshot)
    {
        if (snapshot.Count == 0)
        {
            return;
        }

        builder.Append(label)
            .Append(": avg ")
            .Append(snapshot.Average.ToString("F2"))
            .Append(" ms, p95 ")
            .Append(snapshot.P95.ToString("F2"))
            .Append(" ms, p99 ")
            .Append(snapshot.P99.ToString("F2"))
            .Append(" ms, max ")
            .Append(snapshot.Max.ToString("F2"))
            .AppendLine(" ms");
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

        public int Count => _count;

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
                Percentile(values, 0.99),
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

    private readonly record struct MetricSnapshot(int Count, double Average, double P95, double P99, double Max);
}

public static class StopwatchTickHelpers
{
    public static long GetTimestamp()
    {
        return Stopwatch.GetTimestamp();
    }

    public static double ToMilliseconds(long elapsedTicks)
    {
        return elapsedTicks * 1000.0 / Stopwatch.Frequency;
    }
}
