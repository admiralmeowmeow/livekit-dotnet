using System.Runtime.InteropServices;
using Google.Protobuf;
using LiveKit.Proto;

namespace LiveKitD3D11Viewer.LiveKit;

internal sealed class LiveKitFfiClient : IDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void FfiCallback(IntPtr dataPtr, nuint length);

    private static readonly object ResolverLock = new();
    private static bool _resolverRegistered;
    private static FfiCallback? _callback;
    private static EventInbox? _eventInbox;

    private readonly List<ulong> _ownedHandles = new();
    private readonly object _ownedHandlesLock = new();
    private bool _disposed;

    public LiveKitFfiClient(IReadOnlyList<string> libraryCandidates)
    {
        RegisterResolver(libraryCandidates);
        _eventInbox = new EventInbox();
        _callback = HandleFfiEvent;
        Native.livekit_ffi_initialize(_callback, captureLogs: false, sdk: "dotnet-screen-viewer", sdkVersion: "0.1.0");
    }

    public ulong TrackOwnedHandle(ulong handle)
    {
        if (handle == 0)
        {
            return handle;
        }

        lock (_ownedHandlesLock)
        {
            if (!_ownedHandles.Contains(handle))
            {
                _ownedHandles.Add(handle);
            }
        }

        return handle;
    }

    public void DropOwnedHandle(ulong handle)
    {
        if (handle == 0)
        {
            return;
        }

        lock (_ownedHandlesLock)
        {
            _ownedHandles.Remove(handle);
        }

        Native.livekit_ffi_drop_handle(handle);
    }

    public FfiResponse SendRequest(FfiRequest request)
    {
        ThrowIfDisposed();

        var requestBytes = request.ToByteArray();
        IntPtr responsePtr;
        nuint responseLen;

        unsafe
        {
            fixed (byte* requestPtr = requestBytes)
            {
                var bufferHandle = Native.livekit_ffi_request((IntPtr)requestPtr, (nuint)requestBytes.Length, out responsePtr, out responseLen);
                if (bufferHandle == 0 || responsePtr == IntPtr.Zero || responseLen == 0)
                {
                    throw new InvalidOperationException("livekit_ffi_request returned an invalid response buffer.");
                }

                try
                {
                    var responseBytes = new byte[(int)responseLen];
                    Marshal.Copy(responsePtr, responseBytes, 0, responseBytes.Length);
                    return FfiResponse.Parser.ParseFrom(responseBytes);
                }
                finally
                {
                    Native.livekit_ffi_drop_handle(bufferHandle);
                }
            }
        }
    }

    public Task<FfiEvent> WaitForEventAsync(Func<FfiEvent, bool> predicate, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var inbox = _eventInbox ?? throw new InvalidOperationException("FFI inbox is unavailable.");
        return inbox.WaitForAsync(predicate, cancellationToken);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        List<ulong> handles;
        lock (_ownedHandlesLock)
        {
            handles = _ownedHandles.ToList();
            _ownedHandles.Clear();
        }

        foreach (var handle in handles)
        {
            Native.livekit_ffi_drop_handle(handle);
        }

        Native.livekit_ffi_dispose();
        _eventInbox = null;
        _callback = null;
    }

    private static void RegisterResolver(IReadOnlyList<string> libraryCandidates)
    {
        lock (ResolverLock)
        {
            if (_resolverRegistered)
            {
                return;
            }

            var frozenCandidates = libraryCandidates.ToArray();
            NativeLibrary.SetDllImportResolver(typeof(Native).Assembly, (libraryName, _, _) =>
            {
                if (!string.Equals(libraryName, "livekit_ffi", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(libraryName, "livekit_ffi.dll", StringComparison.OrdinalIgnoreCase))
                {
                    return IntPtr.Zero;
                }

                foreach (var candidate in frozenCandidates)
                {
                    if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var handle))
                    {
                        return handle;
                    }
                }

                if (NativeLibrary.TryLoad("livekit_ffi.dll", out var fallbackHandle))
                {
                    return fallbackHandle;
                }

                throw new DllNotFoundException($"Unable to locate livekit_ffi.dll. Checked: {string.Join(", ", frozenCandidates)}");
            });

            _resolverRegistered = true;
        }
    }

    private static void HandleFfiEvent(IntPtr dataPtr, nuint length)
    {
        var inbox = _eventInbox;
        if (inbox is null || dataPtr == IntPtr.Zero || length == 0)
        {
            return;
        }

        var payload = new byte[(int)length];
        Marshal.Copy(dataPtr, payload, 0, payload.Length);
        var ffiEvent = FfiEvent.Parser.ParseFrom(payload);
        inbox.Enqueue(ffiEvent);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private static class Native
    {
        [DllImport("livekit_ffi", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void livekit_ffi_initialize(FfiCallback callback, [MarshalAs(UnmanagedType.I1)] bool captureLogs, string sdk, string sdkVersion);

        [DllImport("livekit_ffi", CallingConvention = CallingConvention.Cdecl)]
        internal static extern ulong livekit_ffi_request(IntPtr data, nuint len, out IntPtr resPtr, out nuint resLen);

        [DllImport("livekit_ffi", CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        internal static extern bool livekit_ffi_drop_handle(ulong handleId);

        [DllImport("livekit_ffi", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void livekit_ffi_dispose();
    }

    private sealed class EventInbox
    {
        private readonly List<FfiEvent> _backlog = new();
        private readonly SemaphoreSlim _signal = new(0);
        private readonly object _sync = new();

        public void Enqueue(FfiEvent ffiEvent)
        {
            lock (_sync)
            {
                _backlog.Add(ffiEvent);
            }

            _signal.Release();
        }

        public async Task<FfiEvent> WaitForAsync(Func<FfiEvent, bool> predicate, CancellationToken cancellationToken)
        {
            while (true)
            {
                FfiEvent? match = null;
                lock (_sync)
                {
                    for (var index = 0; index < _backlog.Count; index++)
                    {
                        var candidate = _backlog[index];
                        if (!predicate(candidate))
                        {
                            continue;
                        }

                        match = candidate;
                        _backlog.RemoveAt(index);
                        break;
                    }
                }

                if (match is not null)
                {
                    return match;
                }

                await _signal.WaitAsync(cancellationToken);
            }
        }
    }
}
