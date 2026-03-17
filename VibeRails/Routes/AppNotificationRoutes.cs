using System.Text.Json;
using System.Threading.Channels;
using VibeRails.DTOs;
using VibeRails.Interfaces;

namespace VibeRails.Routes;

public sealed class AppNotificationWebSocketHandler : WebSocketHandler
{
    private readonly IAppNotificationService _notificationService;

    public AppNotificationWebSocketHandler(IAppNotificationService notificationService)
    {
        _notificationService = notificationService;
    }

    protected override string RoutePath => "/api/v1/notifications/ws";
    protected override string? RouteName => "AppNotificationStream";

    protected override IDisposable OnConnected(HttpContext context, ChannelWriter<string> writer)
    {
        return _notificationService.SubscribeToToasts(notification =>
        {
            var json = JsonSerializer.Serialize(
                notification,
                AppJsonSerializerContext.Default.AppToastNotification);
            writer.TryWrite(json);
        });
    }
}
