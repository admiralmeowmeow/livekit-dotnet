using LiveKit.Proto;
using LiveKitScreenShareHost.Capture;

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

    public LiveKitPublisher(LiveKitFfiClient ffi, PrimaryScreenCapturer capturer, AppOptions options)
    {
        _ffi = ffi;
        _capturer = capturer;
        _options = options;
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
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(1000d / _options.CaptureFps));

        while (!cancellationToken.IsCancellationRequested && await timer.WaitForNextTickAsync())
        {
            PublishSingleFrame();
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

        using var frame = _capturer.CaptureFrame();
        using var rgbaFrame = frame.CopyAsRgba();
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
    }

    private static void EnsureResponse(bool condition, string errorMessage)
    {
        if (!condition)
        {
            throw new InvalidOperationException(errorMessage);
        }
    }
}
