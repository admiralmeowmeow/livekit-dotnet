using System.Diagnostics;
using System.Net.Sockets;

namespace LiveKitScreenShareHost.Hosting;

internal sealed class HostedLiveKitServer : IAsyncDisposable
{
    private readonly Process _process;

    private HostedLiveKitServer(Process process)
    {
        _process = process;
    }

    public static async Task<HostedLiveKitServer?> StartIfRequestedAsync(AppOptions options, CancellationToken cancellationToken)
    {
        if (!options.HostServer)
        {
            return null;
        }

        var executable = ResolveExecutable(options);
        if (executable is null)
        {
            throw new FileNotFoundException(
                "Could not locate livekit-server.exe. Download the official Windows binary into the tools folder or set LIVEKIT_SERVER_EXE.",
                options.LiveKitServerExe ?? options.BuildServerDownloadPath());
        }

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = "--dev",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
            EnableRaisingEvents = true,
        };

        process.OutputDataReceived += (_, eventArgs) =>
        {
            if (!string.IsNullOrWhiteSpace(eventArgs.Data))
            {
                Console.WriteLine($"[livekit-server] {eventArgs.Data}");
            }
        };

        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (!string.IsNullOrWhiteSpace(eventArgs.Data))
            {
                Console.Error.WriteLine($"[livekit-server] {eventArgs.Data}");
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await WaitForPortAsync(host: "127.0.0.1", port: 7880, timeout: TimeSpan.FromSeconds(10), cancellationToken);
        return new HostedLiveKitServer(process);
    }

    public async ValueTask DisposeAsync()
    {
        if (_process.HasExited)
        {
            _process.Dispose();
            return;
        }

        try
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync();
        }
        finally
        {
            _process.Dispose();
        }
    }

    private static string? ResolveExecutable(AppOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.LiveKitServerExe) && File.Exists(options.LiveKitServerExe))
        {
            return options.LiveKitServerExe;
        }

        var downloaded = options.BuildServerDownloadPath();
        if (File.Exists(downloaded))
        {
            return downloaded;
        }

        var pathSegments = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var segment in pathSegments)
        {
            var candidate = Path.Combine(segment, "livekit-server.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static async Task WaitForPortAsync(string host, int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);

        while (!deadline.IsCancellationRequested)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(host, port, deadline.Token);
                return;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                await Task.Delay(250, deadline.Token);
            }
        }

        throw new TimeoutException($"Timed out waiting for LiveKit server on {host}:{port}.");
    }
}
