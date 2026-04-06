using System.Net;
using System.Text;
using System.Text.Json;

namespace LiveKitScreenShareHost.Viewer;

internal sealed class LocalViewerHost : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly string _viewerHtml;
    private readonly string _configJson;
    private Task? _serverTask;

    public LocalViewerHost(AppOptions options, string viewerToken)
    {
        ViewerUrl = $"http://127.0.0.1:{options.ViewerPort}/";
        _listener.Prefixes.Add(ViewerUrl);

        var viewerPath = Path.Combine(AppContext.BaseDirectory, "Viewer", "index.html");
        if (!File.Exists(viewerPath))
        {
            throw new FileNotFoundException("Viewer page was not found in the application output.", viewerPath);
        }

        _viewerHtml = File.ReadAllText(viewerPath);
        _configJson = JsonSerializer.Serialize(new
        {
            url = options.Url,
            room = options.RoomName,
            token = viewerToken,
            publisherIdentity = options.Identity,
            targetFps = options.CaptureFps,
            pixelFormat = "RGBA",
        });
    }

    public string ViewerUrl { get; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _listener.Start();
        _serverTask = Task.Run(() => RunAsync(cancellationToken), cancellationToken);
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_listener.IsListening)
        {
            _listener.Stop();
        }

        if (_serverTask is not null)
        {
            try
            {
                await _serverTask;
            }
            catch (HttpListenerException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch (OperationCanceledException)
            {
            }
        }

        _listener.Close();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (HttpListenerException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            await HandleRequestAsync(context, cancellationToken);
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        var path = context.Request.Url?.AbsolutePath ?? "/";
        switch (path)
        {
            case "/":
            case "/index.html":
                await WriteResponseAsync(context.Response, "text/html; charset=utf-8", _viewerHtml, cancellationToken);
                break;
            case "/config.json":
                await WriteResponseAsync(context.Response, "application/json; charset=utf-8", _configJson, cancellationToken);
                break;
            default:
                context.Response.StatusCode = 404;
                await WriteResponseAsync(context.Response, "text/plain; charset=utf-8", "Not found.", cancellationToken);
                break;
        }
    }

    private static async Task WriteResponseAsync(HttpListenerResponse response, string contentType, string body, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        response.ContentType = contentType;
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, cancellationToken);
        response.OutputStream.Close();
    }
}
