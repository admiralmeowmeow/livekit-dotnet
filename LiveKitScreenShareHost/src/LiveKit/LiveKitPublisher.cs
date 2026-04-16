using LiveKit.Proto;
using LiveKitScreenShareHost.Capture;
using System.Diagnostics;
using System.Text;

namespace LiveKitScreenShareHost.LiveKit;

internal sealed class LiveKitPublisher : IAsyncDisposable
{
    private readonly LiveKitFfiClient _ffi;
    private readonly PrimaryScreenCapturer _capturer;
    private readonly AppOptions _options;
    private OwnedRoom? _room;
    private OwnedParticipant? _localParticipant;
    private OwnedVideoSource? _videoSource;
    private OwnedTrack? _videoTrack;
    private OwnedTrackPublication? _publication;
    private readonly object _statsLock = new();
    private readonly RollingMetric _captureMetric = new(256);
    private readonly RollingMetric _convertMetric = new(256);
    private readonly RollingMetric _publishMetric = new(256);
    private readonly RollingMetric _loopMetric = new(256);
    private string _lastStatsSummary = "Host stats: waiting for frames";
    private DateTime _lastStatsSampleUtc = DateTime.UtcNow;
    private int _framesPublishedSinceSample;
    private double _publishedFramesPerSecond;

    public LiveKitPublisher(LiveKitFfiClient ffi, PrimaryScreenCapturer capturer, AppOptions options)
    {
        _ffi = ffi;
        _capturer = capturer;
        _options = options;
    }

    public string CurrentStatsSummary
    {
        get
        {
            lock (_statsLock)
            {
                return _lastStatsSummary;
            }
        }
    }

    public async Task StartAsync(string token, CancellationToken cancellationToken)
    {
        var connectRequest = new FfiRequest
        {
            Connect = new ConnectRequest
            {
                Url = _options.Url,
                Token = token,
                Options = new RoomOptions
                {
                    AutoSubscribe = false,
                    AdaptiveStream = false,
                    Dynacast = false,
                    SinglePeerConnection = true,
                    ConnectTimeoutMs = 10_000UL,
                },
            },
        };

        var connectResponse = _ffi.SendRequest(connectRequest);
        EnsureResponse(connectResponse.MessageCase == FfiResponse.MessageOneofCase.Connect, "Expected Connect response.");
        var connectAsyncId = connectResponse.Connect.AsyncId;

        var connectEvent = await _ffi.WaitForEventAsync(
            e => e.MessageCase == FfiEvent.MessageOneofCase.Connect && e.Connect.AsyncId == connectAsyncId,
            cancellationToken);

        EnsureResponse(connectEvent.Connect.MessageCase == ConnectCallback.MessageOneofCase.Result, connectEvent.Connect.Error ?? "LiveKit connect failed.");

        _room = connectEvent.Connect.Result.Room;
        _localParticipant = connectEvent.Connect.Result.LocalParticipant;
        _ffi.TrackOwnedHandle(_room.Handle.Id);
        _ffi.TrackOwnedHandle(_localParticipant.Handle.Id);

        var resolution = _capturer.Resolution;
        var newSourceResponse = _ffi.SendRequest(new FfiRequest
        {
            NewVideoSource = new NewVideoSourceRequest
            {
                Type = VideoSourceType.VideoSourceNative,
                Resolution = new VideoSourceResolution
                {
                    Width = (uint)resolution.Width,
                    Height = (uint)resolution.Height,
                },
                IsScreencast = true,
            },
        });

        EnsureResponse(newSourceResponse.MessageCase == FfiResponse.MessageOneofCase.NewVideoSource, "Expected NewVideoSource response.");
        _videoSource = newSourceResponse.NewVideoSource.Source;
        _ffi.TrackOwnedHandle(_videoSource.Handle.Id);

        var createTrackResponse = _ffi.SendRequest(new FfiRequest
        {
            CreateVideoTrack = new CreateVideoTrackRequest
            {
                Name = "screen-share",
                SourceHandle = _videoSource.Handle.Id,
            },
        });

        EnsureResponse(createTrackResponse.MessageCase == FfiResponse.MessageOneofCase.CreateVideoTrack, "Expected CreateVideoTrack response.");
        _videoTrack = createTrackResponse.CreateVideoTrack.Track;
        _ffi.TrackOwnedHandle(_videoTrack.Handle.Id);

        var publishResponse = _ffi.SendRequest(new FfiRequest
        {
            PublishTrack = new PublishTrackRequest
            {
                LocalParticipantHandle = _localParticipant.Handle.Id,
                TrackHandle = _videoTrack.Handle.Id,
                Options = new TrackPublishOptions
                {
                    Source = TrackSource.SourceScreenshare,
                    VideoCodec = VideoCodec.H264,
                    Simulcast = false,
                    PreconnectBuffer = false,
                    VideoEncoding = new VideoEncoding
                    {
                        MaxFramerate = _options.CaptureFps,
                        MaxBitrate = ComputeTargetBitrate(_capturer.Resolution.Width, _capturer.Resolution.Height, _options.CaptureFps),
                    },
                },
            },
        });

        EnsureResponse(publishResponse.MessageCase == FfiResponse.MessageOneofCase.PublishTrack, "Expected PublishTrack response.");
        var publishAsyncId = publishResponse.PublishTrack.AsyncId;

        var publishEvent = await _ffi.WaitForEventAsync(
            e => e.MessageCase == FfiEvent.MessageOneofCase.PublishTrack && e.PublishTrack.AsyncId == publishAsyncId,
            cancellationToken);

        EnsureResponse(
            publishEvent.PublishTrack.MessageCase == PublishTrackCallback.MessageOneofCase.Publication,
            publishEvent.PublishTrack.Error ?? "Failed to publish the primary screen track.");

        _publication = publishEvent.PublishTrack.Publication;
        _ffi.TrackOwnedHandle(_publication.Handle.Id);
    }

    public async Task RunUntilCancelledAsync(CancellationToken cancellationToken)
    {
        if (_capturer.IsFrameDriven)
        {
            var pacedFrameInterval = TimeSpan.FromSeconds(1d / _options.CaptureFps);
            var nextPacedFrameUtc = DateTime.UtcNow;
            while (!cancellationToken.IsCancellationRequested)
            {
                PublishSingleFrame();

                nextPacedFrameUtc += pacedFrameInterval;
                var delay = nextPacedFrameUtc - DateTime.UtcNow;
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, cancellationToken);
                }
                else if (-delay > pacedFrameInterval)
                {
                    nextPacedFrameUtc = DateTime.UtcNow;
                }
            }

            return;
        }

        var frameInterval = TimeSpan.FromSeconds(1d / _options.CaptureFps);
        var nextFrameUtc = DateTime.UtcNow;

        while (!cancellationToken.IsCancellationRequested)
        {
            var delay = nextFrameUtc - DateTime.UtcNow;
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken);
            }

            PublishSingleFrame();
            nextFrameUtc += frameInterval;

            var lateness = DateTime.UtcNow - nextFrameUtc;
            if (lateness > frameInterval)
            {
                nextFrameUtc = DateTime.UtcNow;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_room is not null)
        {
            var disconnectResponse = _ffi.SendRequest(new FfiRequest
            {
                Disconnect = new DisconnectRequest
                {
                    RoomHandle = _room.Handle.Id,
                    Reason = DisconnectReason.ClientInitiated,
                },
            });

            if (disconnectResponse.MessageCase == FfiResponse.MessageOneofCase.Disconnect)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    await _ffi.WaitForEventAsync(
                        e => e.MessageCase == FfiEvent.MessageOneofCase.Disconnect && e.Disconnect.AsyncId == disconnectResponse.Disconnect.AsyncId,
                        timeout.Token);
                }
                catch (OperationCanceledException)
                {
                }
            }
        }
    }

    private void PublishSingleFrame()
    {
        if (_videoSource is null)
        {
            throw new InvalidOperationException("Video source has not been initialized.");
        }

        var loopStart = Stopwatch.GetTimestamp();
        using var frame = _capturer.CaptureFrame();
        var captureEnd = Stopwatch.GetTimestamp();
        using var rgbaFrame = frame.CopyAsRgba(_capturer.FramePool);
        var convertEnd = Stopwatch.GetTimestamp();
        var bufferInfo = new VideoBufferInfo
        {
            Type = VideoBufferType.Rgba,
            Width = (uint)rgbaFrame.Width,
            Height = (uint)rgbaFrame.Height,
            DataPtr = (ulong)rgbaFrame.DataPointer.ToInt64(),
            Stride = (uint)rgbaFrame.Stride,
        };

        var captureResponse = _ffi.SendRequest(new FfiRequest
        {
            CaptureVideoFrame = new CaptureVideoFrameRequest
            {
                Buffer = bufferInfo,
                SourceHandle = _videoSource.Handle.Id,
                TimestampUs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000,
                Rotation = VideoRotation._0,
            },
        });

        EnsureResponse(captureResponse.MessageCase == FfiResponse.MessageOneofCase.CaptureVideoFrame, "Expected CaptureVideoFrame response.");
        var publishEnd = Stopwatch.GetTimestamp();
        TrackFrame(
            StopwatchTickHelpers.ToMilliseconds(captureEnd - loopStart),
            StopwatchTickHelpers.ToMilliseconds(convertEnd - captureEnd),
            StopwatchTickHelpers.ToMilliseconds(publishEnd - convertEnd),
            StopwatchTickHelpers.ToMilliseconds(publishEnd - loopStart));
    }

    private void TrackFrame(double captureMilliseconds, double convertMilliseconds, double publishMilliseconds, double loopMilliseconds)
    {
        lock (_statsLock)
        {
            _captureMetric.Add(captureMilliseconds);
            _convertMetric.Add(convertMilliseconds);
            _publishMetric.Add(publishMilliseconds);
            _loopMetric.Add(loopMilliseconds);
            _framesPublishedSinceSample++;

            var now = DateTime.UtcNow;
            var elapsed = now - _lastStatsSampleUtc;
            if (elapsed.TotalMilliseconds >= 250)
            {
                _publishedFramesPerSecond = _framesPublishedSinceSample / elapsed.TotalSeconds;
                _framesPublishedSinceSample = 0;
                _lastStatsSampleUtc = now;
            }

            _lastStatsSummary = BuildStatsSummary();
        }
    }

    private string BuildStatsSummary()
    {
        var builder = new StringBuilder();
        builder.Append("Backend: ")
            .Append(_capturer.BackendName)
            .Append(" | Host FPS: ")
            .Append(_publishedFramesPerSecond.ToString("F1"));

        AppendMetric(builder, "capture", _captureMetric.GetSnapshot());
        AppendMetric(builder, "convert", _convertMetric.GetSnapshot());
        AppendMetric(builder, "publish", _publishMetric.GetSnapshot());
        AppendMetric(builder, "loop", _loopMetric.GetSnapshot());
        return builder.ToString();
    }

    private static void AppendMetric(StringBuilder builder, string label, MetricSnapshot snapshot)
    {
        if (snapshot.Count == 0)
        {
            return;
        }

        builder.Append(" | ")
            .Append(label)
            .Append(' ')
            .Append(snapshot.Average.ToString("F1"))
            .Append(" ms avg / ")
            .Append(snapshot.P95.ToString("F1"))
            .Append(" p95");
    }

    private static ulong ComputeTargetBitrate(int width, int height, int fps)
    {
        var estimated = width * height * fps * 0.14;
        return (ulong)Math.Clamp((long)estimated, 8_000_000, 40_000_000);
    }

    private static void EnsureResponse(bool condition, string errorMessage)
    {
        if (!condition)
        {
            throw new InvalidOperationException(errorMessage);
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
            for (var index = 0; index < values.Length; index++)
            {
                sum += values[index];
            }

            return new MetricSnapshot(
                values.Length,
                sum / values.Length,
                Percentile(values, 0.95));
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

    private readonly record struct MetricSnapshot(int Count, double Average, double P95);

    private static class StopwatchTickHelpers
    {
        public static double ToMilliseconds(long elapsedTicks)
        {
            return elapsedTicks * 1000.0 / Stopwatch.Frequency;
        }
    }
}
