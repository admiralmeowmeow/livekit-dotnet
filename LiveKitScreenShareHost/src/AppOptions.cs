namespace LiveKitScreenShareHost;

internal sealed record AppOptions(
    string Url,
    string RoomName,
    string Identity,
    string ParticipantName,
    int ViewerPort,
    string ApiKey,
    string ApiSecret,
    int CaptureFps,
    bool HostServer,
    string? LiveKitServerExe,
    string? LiveKitFfiDll)
{
    public static AppOptions FromEnvironment(string[] args)
    {
        var values = args
            .Select(arg => arg.Split('=', 2, StringSplitOptions.TrimEntries))
            .Where(parts => parts.Length == 2 && parts[0].StartsWith("--", StringComparison.Ordinal))
            .ToDictionary(parts => parts[0][2..], parts => parts[1], StringComparer.OrdinalIgnoreCase);

        var roomName = GetValue(values, "room", "LIVEKIT_ROOM", "screen-room");
        var identity = GetValue(values, "identity", "LIVEKIT_IDENTITY", $"screen-host-{Environment.MachineName.ToLowerInvariant()}");
        var participantName = GetValue(values, "name", "LIVEKIT_NAME", "Primary Screen Host");
        var viewerPort = int.TryParse(GetValue(values, "viewer-port", "LIVEKIT_VIEWER_PORT", "8081"), out var parsedViewerPort)
            ? parsedViewerPort
            : 8081;
        var apiKey = GetValue(values, "api-key", "LIVEKIT_API_KEY", "devkey");
        var apiSecret = GetValue(values, "api-secret", "LIVEKIT_API_SECRET", "secret");
        var url = GetValue(values, "url", "LIVEKIT_URL", "ws://127.0.0.1:7880");
        var serverExe = GetValue(values, "server-exe", "LIVEKIT_SERVER_EXE", null);
        var ffiDll = GetValue(values, "ffi-dll", "LIVEKIT_FFI_DLL", null);
        var fps = int.TryParse(GetValue(values, "fps", "LIVEKIT_FPS", "60"), out var parsedFps) ? parsedFps : 60;
        var hostServer = bool.TryParse(GetValue(values, "host-server", "LIVEKIT_HOST_SERVER", "false"), out var parsedHost)
            ? parsedHost
            : true;

        return new AppOptions(
            Url: url!,
            RoomName: roomName!,
            Identity: identity!,
            ParticipantName: participantName!,
            ViewerPort: Math.Clamp(viewerPort, 1024, 65535),
            ApiKey: apiKey!,
            ApiSecret: apiSecret!,
            CaptureFps: Math.Max(1, fps),
            HostServer: hostServer,
            LiveKitServerExe: serverExe,
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
            candidates.Add(Path.Combine(appRoot, "..", "rust-sdks", "target", "release", "livekit_ffi.dll"));
        }

        return candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public string BuildServerDownloadPath()
    {
        var appRoot = FindProjectRoot(AppContext.BaseDirectory) ?? AppContext.BaseDirectory;
        return Path.Combine(appRoot, "tools", "livekit-server.exe");
    }

    private static string? FindProjectRoot(string startDirectory)
    {
        var current = new DirectoryInfo(startDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "LiveKitScreenShareHost.csproj")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return null;
    }

    private static string? GetValue(IReadOnlyDictionary<string, string> args, string argName, string envName, string? fallback)
    {
        if (args.TryGetValue(argName, out var argValue) && !string.IsNullOrWhiteSpace(argValue))
        {
            return argValue;
        }

        var envValue = Environment.GetEnvironmentVariable(envName);
        if (!string.IsNullOrWhiteSpace(envValue))
        {
            return envValue;
        }

        return fallback;
    }
}
