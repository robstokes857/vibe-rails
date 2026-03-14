using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using VibeRails.DTOs;
using VibeRails.Services;
using VibeRails.Services.Terminal;

namespace VibeRails.Routes;

public static class EventRoutes
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        TypeInfoResolver = AppJsonSerializerContext.Default
    };

    public static void Map(WebApplication app)
    {
        app.Map("/api/v1/events/ws", async (HttpContext context, EventBus eventBus, CancellationToken cancellationToken) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                return;
            }

            // Subscribers may opt-in to a specific child tab's events via ?tabId=<id>.
            // Omitting the parameter delivers all events (main process + all child tabs).
            var tabIdFilter = context.Request.Query.TryGetValue("tabId", out var tv) ? tv.ToString() : null;

            var acceptedSubprotocol = context.Items["viberails_accepted_subprotocol"] as string;
            using var ws = await context.WebSockets.AcceptWebSocketAsync(acceptedSubprotocol);

            // Bounded channel: if the consumer falls behind, old events are dropped instead of
            // accumulating unbounded memory.
            var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(256)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true
            });

            void OnEvent(string type, string text, string? tabId)
            {
                if (tabIdFilter != null && tabId != tabIdFilter)
                    return;

                var json = JsonSerializer.Serialize(new EventMessage(type, text), AppJsonSerializerContext.Default.EventMessage);
                channel.Writer.TryWrite(json);
            }

            eventBus.OnEvent += OnEvent;
            try
            {
                // Writer: drain channel and send over WebSocket
                var writerTask = Task.Run(async () =>
                {
                    await foreach (var json in channel.Reader.ReadAllAsync(cancellationToken))
                    {
                        if (ws.State != WebSocketState.Open)
                            break;

                        var bytes = Encoding.UTF8.GetBytes(json);
                        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
                    }
                }, cancellationToken);

                // Reader: drain incoming frames to detect close
                var buffer = new byte[1024];
                while (ws.State == WebSocketState.Open)
                {
                    var result = await ws.ReceiveAsync(buffer, cancellationToken);
                    if (result.MessageType == WebSocketMessageType.Close)
                        break;
                }

                channel.Writer.TryComplete();
                await writerTask;
            }
            catch (OperationCanceledException) { }
            catch (WebSocketException) { }
            finally
            {
                eventBus.OnEvent -= OnEvent;
                channel.Writer.TryComplete();

                if (ws.State == WebSocketState.Open)
                {
                    try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closed", CancellationToken.None); }
                    catch { }
                }
            }
        });

        // Proxy events from a child tab process (port 5002 → port 500N)
        // The parent already holds the child's session token and tab token.
        app.Map("/api/v1/events/tab/{tabId}/ws", async (HttpContext context, string tabId, ITerminalTabHostService tabHost, CancellationToken cancellationToken) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                return;
            }

            var acceptedSubprotocol = context.Items["viberails_accepted_subprotocol"] as string;
            using var browserSocket = await context.WebSockets.AcceptWebSocketAsync(acceptedSubprotocol);
            await tabHost.HandleEventWebSocketProxyAsync(tabId, browserSocket, cancellationToken);
        });
    }
}
