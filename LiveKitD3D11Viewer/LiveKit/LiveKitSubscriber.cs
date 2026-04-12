using System.Runtime.InteropServices;
using System.Diagnostics;
using LiveKit.Proto;

namespace LiveKitD3D11Viewer.LiveKit;

internal sealed class LiveKitSubscriber : IAsyncDisposable
{
    private static readonly string StatusLogPath = Path.Combine(AppContext.BaseDirectory, "viewer-status.log");
    private readonly LiveKitFfiClient _ffi;
    private readonly AppOptions _options;
    private readonly LiveKitFrameBridge _frameBridge;
    private readonly Dictionary<string, ulong> _participantHandles = new(StringComparer.Ordinal);
    private readonly HashSet<ulong> _enabledPublicationHandles = [];
    private readonly Dictionary<ulong, OwnedVideoStream> _videoStreams = [];

    private OwnedRoom? _room;
    private OwnedParticipant? _localParticipant;
    private Task? _eventLoopTask;
    private CancellationTokenSource? _eventLoopCts;
    private ulong? _activeTrackHandle;
    private string? _activeParticipantIdentity;
    private ulong? _preferredVideoStreamHandle;
    private long _liveFrameIndex;
    private DateTimeOffset? _lastLiveFrameAtUtc;
    private int _ignoredVideoStreamEventCount;
    private int _videoStreamEventLogCount;
    private int _roomEventLogCount;

    public LiveKitSubscriber(LiveKitFfiClient ffi, AppOptions options, LiveKitFrameBridge frameBridge)
    {
        _ffi = ffi;
        _options = options;
        _frameBridge = frameBridge;
    }

    public event EventHandler<string>? StatusChanged;

    public async Task StartAsync(string token, CancellationToken cancellationToken)
    {
        RaiseStatus($"Connecting to {_options.Url} / room '{_options.RoomName}'...");

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
                    // Use the default subscriber transport path so remote screenshare
                    // subscriptions negotiate on a dedicated receive connection.
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

        _room = connectEvent.Connect.Result.Room;
        _localParticipant = connectEvent.Connect.Result.LocalParticipant;
        _ffi.TrackOwnedHandle(_room.Handle.Id);
        _ffi.TrackOwnedHandle(_localParticipant.Handle.Id);

        RaiseStatus($"Connected. Local identity '{_localParticipant.Info?.Identity ?? _options.Identity}'. Waiting for screenshare track...");

        SeedExistingParticipants(connectEvent.Connect.Result.Participants);

        _eventLoopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _eventLoopTask = Task.Run(() => RunEventLoopAsync(_eventLoopCts.Token), _eventLoopCts.Token);
    }

    public async ValueTask DisposeAsync()
    {
        if (_eventLoopCts is not null)
        {
            _eventLoopCts.Cancel();
        }

        if (_eventLoopTask is not null)
        {
            try
            {
                await _eventLoopTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

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

        DropAllVideoStreams();
        _eventLoopCts?.Dispose();
    }

    private async Task RunEventLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var ffiEvent = await _ffi.WaitForEventAsync(
                e => e.MessageCase is FfiEvent.MessageOneofCase.RoomEvent
                    or FfiEvent.MessageOneofCase.VideoStreamEvent
                    or FfiEvent.MessageOneofCase.Disconnect
                    or FfiEvent.MessageOneofCase.Logs,
                cancellationToken);

            switch (ffiEvent.MessageCase)
            {
                case FfiEvent.MessageOneofCase.RoomEvent:
                    LogRoomEvent(ffiEvent.RoomEvent);
                    await HandleRoomEventAsync(ffiEvent.RoomEvent, cancellationToken);
                    break;

                case FfiEvent.MessageOneofCase.VideoStreamEvent:
                    HandleVideoStreamEvent(ffiEvent.VideoStreamEvent);
                    break;

                case FfiEvent.MessageOneofCase.Disconnect:
                    RaiseStatus("Disconnected from LiveKit.");
                    return;

                case FfiEvent.MessageOneofCase.Logs:
                    LogFfiMessage(ffiEvent.Logs);
                    break;
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
                RaiseStatus($"Track subscription failed: {roomEvent.TrackSubscriptionFailed?.Error ?? "unknown error"}");
                break;

            case RoomEvent.MessageOneofCase.TrackPublished:
                await HandleTrackPublishedAsync(roomEvent.TrackPublished, cancellationToken);
                break;

            case RoomEvent.MessageOneofCase.TrackUnpublished:
                RaiseStatus($"Track unpublished by '{roomEvent.TrackUnpublished?.ParticipantIdentity}'.");
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
        _participantHandles[identity] = participant.Handle.Id;
        RaiseStatus($"Participant connected: '{identity}'.");
    }

    private void SeedExistingParticipants(IEnumerable<ConnectCallback.Types.ParticipantWithTracks> participants)
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
            _participantHandles[identity] = participant.Handle.Id;

            var screensharePublication = participantWithTracks.Publications
                .FirstOrDefault(publication =>
                    publication?.Info is not null &&
                    publication.Handle is not null &&
                    publication.Info.Kind == TrackKind.KindVideo &&
                    publication.Info.Source == TrackSource.SourceScreenshare);

            if (screensharePublication is null)
            {
                RaiseStatus($"Participant '{identity}' is present, but no existing screenshare publication was found.");
                continue;
            }

            _ffi.TrackOwnedHandle(screensharePublication.Handle.Id);
            EnableScreensharePublication(identity, screensharePublication.Handle.Id);
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

        RaiseStatus(
            $"Track subscribed: participant='{participantIdentity}', kind={trackInfo.Kind}, trackHandle={trackSubscribed.Track.Handle.Id}.");

        if (trackInfo.Kind != TrackKind.KindVideo)
        {
            return;
        }

        await SubscribeToTrackAsync(trackSubscribed.Track.Handle.Id, participantIdentity, cancellationToken);
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
        RaiseStatus($"Track published by '{participantIdentity}': source={publicationInfo.Source}, kind={publicationInfo.Kind}.");

        if (publicationInfo.Kind != TrackKind.KindVideo || publicationInfo.Source != TrackSource.SourceScreenshare)
        {
            return;
        }

        EnableScreensharePublication(participantIdentity, publication.Handle.Id);
        await Task.CompletedTask;
    }

    private void EnableScreensharePublication(string participantIdentity, ulong publicationHandle)
    {
        if (!_enabledPublicationHandles.Add(publicationHandle))
        {
            RaiseStatus($"Screenshare publication for '{participantIdentity}' is already enabled. Waiting for track subscription...");
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

        RaiseStatus($"Subscribed to screenshare publication for '{participantIdentity}'. Waiting for TrackSubscribed...");
    }

    private Task SubscribeToTrackAsync(ulong trackHandle, string participantIdentity, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_activeTrackHandle == trackHandle && _videoStreams.Count > 0)
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
        RegisterVideoStream(stream, prefer: _preferredVideoStreamHandle is null);
        _activeTrackHandle = trackHandle;
        _activeParticipantIdentity = participantIdentity;

        RaiseStatus($"Opened track video stream for '{participantIdentity}' (track handle {trackHandle}, stream handle {stream.Handle.Id}).");
        return Task.CompletedTask;
    }

    private void HandleVideoStreamEvent(VideoStreamEvent streamEvent)
    {
        if (_videoStreamEventLogCount < 24)
        {
            _videoStreamEventLogCount++;
            Debug.WriteLine(
                $"[LiveKitViewer] VideoStreamEvent case={streamEvent.MessageCase}, stream={streamEvent.StreamHandle}, " +
                $"preferred={_preferredVideoStreamHandle?.ToString() ?? "none"}, known={DescribeKnownStreamHandles()}");
        }

        if (!_videoStreams.ContainsKey(streamEvent.StreamHandle))
        {
            if (_ignoredVideoStreamEventCount < 6)
            {
                _ignoredVideoStreamEventCount++;
                RaiseStatus($"Ignoring video stream event for unknown handle {streamEvent.StreamHandle} (active: {DescribeKnownStreamHandles()}).");
            }

            return;
        }

        switch (streamEvent.MessageCase)
        {
            case VideoStreamEvent.MessageOneofCase.FrameReceived:
                _preferredVideoStreamHandle = streamEvent.StreamHandle;
                HandleFrameReceived(streamEvent.FrameReceived);
                break;

            case VideoStreamEvent.MessageOneofCase.Eos:
                DropVideoStream(streamEvent.StreamHandle);
                if (_videoStreams.Count == 0)
                {
                    _preferredVideoStreamHandle = null;
                    _activeTrackHandle = null;
                    _activeParticipantIdentity = null;
                    RaiseStatus("Live video stream ended. Falling back to synthetic feed.");
                }
                else
                {
                    RaiseStatus($"Video stream handle {streamEvent.StreamHandle} ended. Remaining handles: {DescribeKnownStreamHandles()}.");
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
                RaiseStatus($"Received non-renderable frame: type={info.Type}, size={info.Width}x{info.Height}, stride={info.Stride}.");
                return;
            }

            var byteLength = checked((int)(info.Stride * info.Height));
            var frameIndex = Interlocked.Increment(ref _liveFrameIndex);
            _lastLiveFrameAtUtc = DateTimeOffset.UtcNow;
            if (frameIndex == 1 || frameIndex % 120 == 0)
            {
                RaiseStatus($"Receiving live RGBA frames: {info.Width}x{info.Height}, stride {info.Stride}, frame #{frameIndex}.");
            }

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

    private void RegisterVideoStream(OwnedVideoStream? stream, bool prefer)
    {
        if (stream?.Handle is null)
        {
            return;
        }

        _ffi.TrackOwnedHandle(stream.Handle.Id);
        _videoStreams[stream.Handle.Id] = stream;
        if (prefer || _preferredVideoStreamHandle is null)
        {
            _preferredVideoStreamHandle = stream.Handle.Id;
        }
    }

    private void DropVideoStream(ulong streamHandle)
    {
        if (_videoStreams.Remove(streamHandle))
        {
            _ffi.DropOwnedHandle(streamHandle);
        }
    }

    private void DropAllVideoStreams()
    {
        foreach (var streamHandle in _videoStreams.Keys.ToArray())
        {
            _ffi.DropOwnedHandle(streamHandle);
        }

        _videoStreams.Clear();
        _preferredVideoStreamHandle = null;
    }

    private string DescribeKnownStreamHandles()
    {
        return _videoStreams.Count == 0
            ? "none"
            : string.Join(", ", _videoStreams.Keys.OrderBy(static handle => handle));
    }

    private void LogRoomEvent(RoomEvent roomEvent)
    {
        if (_roomEventLogCount >= 24)
        {
            return;
        }

        _roomEventLogCount++;
        LogLine($"[LiveKitViewer] RoomEvent case={roomEvent.MessageCase}");
    }

    private void LogFfiMessage(LogBatch? batch)
    {
        if (batch is null)
        {
            return;
        }

        foreach (var record in batch.Records)
        {
            var message = $"[LiveKitViewer][FFI:{record.Level}] {record.Message}";
            LogLine(message);

            if (record.Level is LogLevel.LogError or LogLevel.LogWarn)
            {
                RaiseStatus(record.Message);
            }
        }
    }

    private void RaiseStatus(string status)
    {
        LogLine($"[LiveKitViewer][Status] {status}");
        StatusChanged?.Invoke(this, status);
    }

    private static void LogLine(string message)
    {
        Debug.WriteLine(message);

        try
        {
            File.AppendAllText(StatusLogPath, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
        catch
        {
        }
    }

    private static void EnsureResponse(bool condition, string errorMessage)
    {
        if (!condition)
        {
            throw new InvalidOperationException(errorMessage);
        }
    }
}


