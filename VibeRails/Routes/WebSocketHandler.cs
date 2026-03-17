using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;

namespace VibeRails.Routes;

/// <summary>
/// Abstract base class for server-push WebSocket endpoints.
/// Handles: WebSocket acceptance with subprotocol, bounded channel, writer task,
/// reader loop (close detection + optional message handling), and cleanup.
/// Auth is handled upstream by CookieAuthMiddleware.
/// </summary>
public abstract class WebSocketHandler
{
    /// <summary>
    /// The route path for this WebSocket endpoint (e.g., "/api/v1/notifications/ws").
    /// </summary>
    protected abstract string RoutePath { get; }

    /// <summary>
    /// Optional route name for WithName(). Return null to skip.
    /// </summary>
    protected virtual string? RouteName => null;

    /// <summary>
    /// Bounded channel capacity for outbound messages. Default 64.
    /// </summary>
    protected virtual int ChannelCapacity => 64;

    /// <summary>
    /// Called when a WebSocket connection is established. Subclasses subscribe to
    /// their event source and write pre-serialized JSON strings to the channel writer.
    /// Return an IDisposable to clean up the subscription when the connection closes.
    /// </summary>
    protected abstract IDisposable OnConnected(HttpContext context, ChannelWriter<string> writer);

    /// <summary>
    /// Called when a text message is received from the client.
    /// Default implementation ignores it (server-push pattern).
    /// Override for bidirectional communication.
    /// </summary>
    protected virtual Task OnMessageReceivedAsync(string message, WebSocket webSocket, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// Registers this handler's WebSocket route on the application.
    /// Call from Routes.cs on the singleton instance resolved from DI.
    /// </summary>
    public void MapWebSocket(WebApplication app)
    {
        var routeBuilder = app.Map(RoutePath, (HttpContext context) => HandleRequestAsync(context));
        routeBuilder.ExcludeFromDescription();
        if (RouteName != null)
        {
            routeBuilder.WithName(RouteName);
        }
    }

    private async Task HandleRequestAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = 400;
            return;
        }

        var acceptedSubprotocol = context.Items["viberails_accepted_subprotocol"] as string;
        using var ws = await context.WebSockets.AcceptWebSocketAsync(acceptedSubprotocol);

        var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true
        });

        var cancellationToken = context.RequestAborted;
        using var subscription = OnConnected(context, channel.Writer);

        try
        {
            var writerTask = Task.Run(async () =>
            {
                await foreach (var message in channel.Reader.ReadAllAsync(cancellationToken))
                {
                    if (ws.State != WebSocketState.Open)
                        break;

                    var bytes = Encoding.UTF8.GetBytes(message);
                    await ws.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
                }
            }, cancellationToken);

            var buffer = new byte[4096];
            while (ws.State == WebSocketState.Open)
            {
                var result = await ws.ReceiveAsync(buffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                    break;

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    await OnMessageReceivedAsync(text, ws, cancellationToken);
                }
            }

            channel.Writer.TryComplete();
            await writerTask;
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        finally
        {
            channel.Writer.TryComplete();

            if (ws.State == WebSocketState.Open)
            {
                try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closed", CancellationToken.None); }
                catch { }
            }
        }
    }
}
