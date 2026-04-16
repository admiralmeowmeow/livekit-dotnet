using LiveKit.Proto;

namespace LiveKitD3D11Viewer.LiveKit;

internal sealed class LiveKitSubscriber : IAsyncDisposable
{
    private readonly LiveKitFfiClient _ffi;
    private readonly AppOptions _options;
    private readonly LiveKitFrameBridge _frameBridge;
    private readonly HashSet<ulong> _enabledPublicationHandles = [];
    private readonly HashSet<ulong> _videoStreamHandles = [];

    private ulong _connectedRoomHandle;
    private Task? _roomEventLoopTask;
    private CancellationTokenSource? _roomEventLoopCts;
    private ulong? _subscribedTrackHandle;
    private long _liveFrameIndex;

    public LiveKitSubscriber(LiveKitFfiClient ffi, AppOptions options, LiveKitFrameBridge frameBridge)
    {
        _ffi = ffi;
        _options = options;
        _frameBridge = frameBridge;
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
                    SinglePeerConnection = false,
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

        var room = connectEvent.Connect.Result.Room;
        var localParticipant = connectEvent.Connect.Result.LocalParticipant;
        if (room?.Handle is not null)
        {
            _connectedRoomHandle = _ffi.TrackOwnedHandle(room.Handle.Id);
        }

        if (localParticipant?.Handle is not null)
        {
            _ffi.TrackOwnedHandle(localParticipant.Handle.Id);
        }

        SubscribeToExistingScreenshares(connectEvent.Connect.Result.Participants);

        _roomEventLoopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _roomEventLoopTask = Task.Run(() => RunEventLoopAsync(_roomEventLoopCts.Token), _roomEventLoopCts.Token);
    }

    public async ValueTask DisposeAsync()
    {
        if (_roomEventLoopCts is not null)
        {
            _roomEventLoopCts.Cancel();
        }

        if (_roomEventLoopTask is not null)
        {
            try
            {
                await _roomEventLoopTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        if (_connectedRoomHandle != 0)
        {
            var disconnectResponse = _ffi.SendRequest(new FfiRequest
            {
                Disconnect = new DisconnectRequest
                {
                    RoomHandle = _connectedRoomHandle,
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

        DropAllVideoStreams();
        _roomEventLoopCts?.Dispose();
    }

    private async Task RunEventLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var ffiEvent = await _ffi.WaitForEventAsync(
                e => e.MessageCase is FfiEvent.MessageOneofCase.RoomEvent
                    or FfiEvent.MessageOneofCase.VideoStreamEvent
                    or FfiEvent.MessageOneofCase.Disconnect,
                cancellationToken);

            switch (ffiEvent.MessageCase)
            {
                case FfiEvent.MessageOneofCase.RoomEvent:
                    await HandleRoomEventAsync(ffiEvent.RoomEvent, cancellationToken);
                    break;

                case FfiEvent.MessageOneofCase.VideoStreamEvent:
                    HandleVideoStreamEvent(ffiEvent.VideoStreamEvent);
                    break;

                case FfiEvent.MessageOneofCase.Disconnect:
                    return;
            }
        }
    }

    private async Task HandleRoomEventAsync(RoomEvent roomEvent, CancellationToken cancellationToken)
    {
        switch (roomEvent.MessageCase)
        {
            case RoomEvent.MessageOneofCase.ParticipantConnected:
                HandleParticipantConnected(roomEvent.ParticipantConnected);
                break;

            case RoomEvent.MessageOneofCase.TrackSubscribed:
                await HandleTrackSubscribedAsync(roomEvent.TrackSubscribed, cancellationToken);
                break;

            case RoomEvent.MessageOneofCase.TrackSubscriptionFailed:
                break;

            case RoomEvent.MessageOneofCase.TrackPublished:
                await HandleTrackPublishedAsync(roomEvent.TrackPublished, cancellationToken);
                break;
        }
    }

    private void HandleParticipantConnected(ParticipantConnected? participantConnected)
    {
        var participant = participantConnected?.Info;
        var identity = participant?.Info?.Identity;
        if (participant?.Handle is null || string.IsNullOrWhiteSpace(identity))
        {
            return;
        }

        _ffi.TrackOwnedHandle(participant.Handle.Id);
    }

    private void SubscribeToExistingScreenshares(IEnumerable<ConnectCallback.Types.ParticipantWithTracks> participants)
    {
        foreach (var participantWithTracks in participants)
        {
            var participant = participantWithTracks.Participant;
            var identity = participant?.Info?.Identity;
            if (participant?.Handle is null || string.IsNullOrWhiteSpace(identity))
            {
                continue;
            }

            _ffi.TrackOwnedHandle(participant.Handle.Id);

            var screensharePublication = participantWithTracks.Publications
                .FirstOrDefault(publication =>
                    publication?.Info is not null &&
                    publication.Handle is not null &&
                    publication.Info.Kind == TrackKind.KindVideo &&
                    publication.Info.Source == TrackSource.SourceScreenshare);

            if (screensharePublication is null)
            {
                continue;
            }

            _ffi.TrackOwnedHandle(screensharePublication.Handle.Id);
            SubscribeToScreensharePublication(identity, screensharePublication.Handle.Id);
        }
    }

    private async Task HandleTrackSubscribedAsync(TrackSubscribed? trackSubscribed, CancellationToken cancellationToken)
    {
        if (trackSubscribed?.Track?.Info is null || trackSubscribed.Track.Handle is null)
        {
            return;
        }

        _ffi.TrackOwnedHandle(trackSubscribed.Track.Handle.Id);
        var trackInfo = trackSubscribed.Track.Info;
        var participantIdentity = trackSubscribed.ParticipantIdentity;

        if (trackInfo.Kind != TrackKind.KindVideo)
        {
            return;
        }

        await OpenVideoStreamAsync(trackSubscribed.Track.Handle.Id, participantIdentity, cancellationToken);
    }

    private async Task HandleTrackPublishedAsync(TrackPublished? trackPublished, CancellationToken cancellationToken)
    {
        var publication = trackPublished?.Publication;
        var publicationInfo = publication?.Info;
        var participantIdentity = trackPublished?.ParticipantIdentity;

        if (publication?.Handle is null || publicationInfo is null || string.IsNullOrWhiteSpace(participantIdentity))
        {
            return;
        }

        _ffi.TrackOwnedHandle(publication.Handle.Id);
        if (publicationInfo.Kind != TrackKind.KindVideo || publicationInfo.Source != TrackSource.SourceScreenshare)
        {
            return;
        }

        SubscribeToScreensharePublication(participantIdentity, publication.Handle.Id);
        await Task.CompletedTask;
    }

    private void SubscribeToScreensharePublication(string participantIdentity, ulong publicationHandle)
    {
        if (!_enabledPublicationHandles.Add(publicationHandle))
        {
            return;
        }

        var subscribeResponse = _ffi.SendRequest(new FfiRequest
        {
            SetSubscribed = new SetSubscribedRequest
            {
                Subscribe = true,
                PublicationHandle = publicationHandle,
            },
        });

        EnsureResponse(
            subscribeResponse.MessageCase == FfiResponse.MessageOneofCase.SetSubscribed,
            "Expected SetSubscribed response.");
    }

    private Task OpenVideoStreamAsync(ulong trackHandle, string participantIdentity, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_subscribedTrackHandle == trackHandle && _videoStreamHandles.Count > 0)
        {
            return Task.CompletedTask;
        }

        var enableResponse = _ffi.SendRequest(new FfiRequest
        {
            EnableRemoteTrack = new EnableRemoteTrackRequest
            {
                TrackHandle = trackHandle,
                Enabled = true,
            },
        });

        EnsureResponse(enableResponse.MessageCase == FfiResponse.MessageOneofCase.EnableRemoteTrack, "Expected EnableRemoteTrack response.");

        var response = _ffi.SendRequest(new FfiRequest
        {
            NewVideoStream = new NewVideoStreamRequest
            {
                TrackHandle = trackHandle,
                Type = VideoStreamType.VideoStreamNative,
                Format = VideoBufferType.Rgba,
                NormalizeStride = true,
                QueueSizeFrames = 2,
            },
        });

        EnsureResponse(response.MessageCase == FfiResponse.MessageOneofCase.NewVideoStream, "Expected NewVideoStream response.");

        var stream = response.NewVideoStream.Stream;
        TrackVideoStreamHandle(stream);
        _subscribedTrackHandle = trackHandle;
        return Task.CompletedTask;
    }

    private void HandleVideoStreamEvent(VideoStreamEvent streamEvent)
    {
        if (!_videoStreamHandles.Contains(streamEvent.StreamHandle))
        {
            return;
        }

        switch (streamEvent.MessageCase)
        {
            case VideoStreamEvent.MessageOneofCase.FrameReceived:
                HandleFrameReceived(streamEvent.FrameReceived);
                break;

            case VideoStreamEvent.MessageOneofCase.Eos:
                DropVideoStream(streamEvent.StreamHandle);
                if (_videoStreamHandles.Count == 0)
                {
                    _subscribedTrackHandle = null;
                }
                break;
        }
    }

    private void HandleFrameReceived(VideoFrameReceived frameReceived)
    {
        var ownedBuffer = frameReceived.Buffer;
        if (ownedBuffer?.Info is null || ownedBuffer.Handle is null)
        {
            return;
        }

        try
        {
            var info = ownedBuffer.Info;
            if (info.Type != VideoBufferType.Rgba || info.DataPtr == 0 || info.Width == 0 || info.Height == 0 || info.Stride == 0)
            {
                return;
            }

            var byteLength = checked((int)(info.Stride * info.Height));
            var frameIndex = Interlocked.Increment(ref _liveFrameIndex);
            _frameBridge.SubmitRgbaFrame(
                new IntPtr((long)info.DataPtr),
                byteLength,
                checked((int)info.Width),
                checked((int)info.Height),
                checked((int)info.Stride),
                frameIndex);
        }
        finally
        {
            _ffi.DropOwnedHandle(ownedBuffer.Handle.Id);
        }
    }

    private void TrackVideoStreamHandle(OwnedVideoStream? stream)
    {
        if (stream?.Handle is null)
        {
            return;
        }

        _ffi.TrackOwnedHandle(stream.Handle.Id);
        _videoStreamHandles.Add(stream.Handle.Id);
    }

    private void DropVideoStream(ulong streamHandle)
    {
        if (_videoStreamHandles.Remove(streamHandle))
        {
            _ffi.DropOwnedHandle(streamHandle);
        }
    }

    private void DropAllVideoStreams()
    {
        foreach (var streamHandle in _videoStreamHandles.ToArray())
        {
            _ffi.DropOwnedHandle(streamHandle);
        }

        _videoStreamHandles.Clear();
    }

    private static void EnsureResponse(bool condition, string errorMessage)
    {
        if (!condition)
        {
            throw new InvalidOperationException(errorMessage);
        }
    }
}


