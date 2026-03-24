using System.Text.Json;
using System.Threading.Channels;
using VibeRails.DTOs;
using VibeRails.Interfaces;

namespace VibeRails.Routes;

public sealed class AppEventWebSocketHandler : WebSocketHandler
{
    private readonly IAppEventBus _eventBus;

    public AppEventWebSocketHandler(IAppEventBus eventBus)
    {
        _eventBus = eventBus;
    }

    protected override string RoutePath => "/api/v1/events/ws";
    protected override string? RouteName => "AppEventStream";

    protected override IDisposable OnConnected(HttpContext context, ChannelWriter<string> writer)
    {
        return _eventBus.Subscribe(appEvent =>
        {
            var json = JsonSerializer.Serialize(appEvent, AppJsonSerializerContext.Default.AppEvent);
            writer.TryWrite(json);
        });
    }
}
