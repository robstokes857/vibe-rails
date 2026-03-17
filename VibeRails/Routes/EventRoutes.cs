#if DEBUG

using System.Text.Json;
using System.Threading.Channels;
using VibeRails.DTOs;
using VibeRails.Services;
using VibeRails.Services.Terminal;

namespace VibeRails.Routes;

public sealed class EventWebSocketHandler : WebSocketHandler
{
    private readonly EventBus _eventBus;

    public EventWebSocketHandler(EventBus eventBus)
    {
        _eventBus = eventBus;
    }

    protected override string RoutePath => "/api/v1/events/ws";
    protected override int ChannelCapacity => 256;

    protected override IDisposable OnConnected(HttpContext context, ChannelWriter<string> writer)
    {
        var tabIdFilter = context.Request.Query.TryGetValue("tabId", out var tv) ? tv.ToString() : null;

        void OnEvent(string type, string text, string? tabId)
        {
            if (tabIdFilter != null && tabId != tabIdFilter)
                return;

            var json = JsonSerializer.Serialize(
                new EventMessage(type, text),
                AppJsonSerializerContext.Default.EventMessage);
            writer.TryWrite(json);
        }

        _eventBus.OnEvent += OnEvent;
        return new EventSubscription(_eventBus, OnEvent);
    }

    private sealed class EventSubscription(EventBus bus, Action<string, string, string?> handler) : IDisposable
    {
        public void Dispose() => bus.OnEvent -= handler;
    }
}

/// <summary>
/// Proxy events from a child tab process — remains as a simple static route
/// since it delegates to tabHost.HandleEventWebSocketProxyAsync (bidirectional).
/// </summary>
public static class EventTabProxyRoutes
{
    public static void Map(WebApplication app)
    {
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

#endif
