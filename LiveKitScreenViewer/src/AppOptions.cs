namespace LiveKitScreenViewer;

internal sealed record AppOptions(
    string Url,
    string RoomName,
    string Identity,
    string ParticipantName,
    string ApiKey,
    string ApiSecret,
    string? LiveKitFfiDll)
{
    public static AppOptions FromEnvironment()
    {
        var roomName = GetValue("LIVEKIT_ROOM", "screen-room");
        var identity = GetValue("LIVEKIT_IDENTITY", $"screen-viewer-{Environment.MachineName.ToLowerInvariant()}");
        var participantName = GetValue("LIVEKIT_NAME", "LiveKit Screen Viewer");
        var apiKey = GetValue("LIVEKIT_API_KEY", "devkey");
        var apiSecret = GetValue("LIVEKIT_API_SECRET", "secret");
        var url = GetValue("LIVEKIT_URL", "ws://127.0.0.1:7880");
        var ffiDll = GetValue("LIVEKIT_FFI_DLL", null);

        return new AppOptions(
            Url: url!,
            RoomName: roomName!,
            Identity: identity!,
            ParticipantName: participantName!,
            ApiKey: apiKey!,
            ApiSecret: apiSecret!,
            LiveKitFfiDll: ffiDll);
    }

    public IReadOnlyList<string> ResolveFfiLibraryCandidates()
    {
        var candidates = new List<string>();

        if (!string.IsNullOrWhiteSpace(LiveKitFfiDll))
        {
            candidates.Add(LiveKitFfiDll!);
        }

        var baseDirectory = AppContext.BaseDirectory;
        candidates.Add(Path.Combine(baseDirectory, "native", "livekit_ffi.dll"));
        candidates.Add(Path.Combine(baseDirectory, "livekit_ffi.dll"));

        var appRoot = FindProjectRoot(baseDirectory);
        if (appRoot is not null)
        {
            candidates.Add(Path.Combine(appRoot, "native", "livekit_ffi.dll"));
            candidates.Add(Path.Combine(appRoot, "..", "..", "LiveKitScreenShareHost", "src", "native", "livekit_ffi.dll"));
            candidates.Add(Path.Combine(appRoot, "..", "..", "rust-sdks", "target", "release", "livekit_ffi.dll"));
        }

        return candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string? FindProjectRoot(string startDirectory)
    {
        var current = new DirectoryInfo(startDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "LiveKitScreenViewer.csproj")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return null;
    }

    private static string? GetValue(string envName, string? fallback)
    {
        var envValue = Environment.GetEnvironmentVariable(envName);
        return !string.IsNullOrWhiteSpace(envValue) ? envValue : fallback;
    }
}
