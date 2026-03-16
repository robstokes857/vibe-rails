using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using VibeRails.DTOs;
using VibeRails.Interfaces;

namespace VibeRails.Routes;

public static class AppNotificationRoutes
{
    public static void Map(WebApplication app)
    {
        app.Map("/api/v1/notifications/ws", async (
            HttpContext context,
            IAppNotificationService notificationService,
            CancellationToken cancellationToken) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                return;
            }

            var acceptedSubprotocol = context.Items["viberails_accepted_subprotocol"] as string;
            using var ws = await context.WebSockets.AcceptWebSocketAsync(acceptedSubprotocol);

            var channel = Channel.CreateBounded<AppToastNotification>(new BoundedChannelOptions(64)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true
            });

            using var subscription = notificationService.SubscribeToToasts(notification =>
            {
                channel.Writer.TryWrite(notification);
            });

            try
            {
                var writerTask = Task.Run(async () =>
                {
                    await foreach (var notification in channel.Reader.ReadAllAsync(cancellationToken))
                    {
                        if (ws.State != WebSocketState.Open)
                        {
                            break;
                        }

                        var json = JsonSerializer.Serialize(
                            notification,
                            AppJsonSerializerContext.Default.AppToastNotification);
                        var bytes = Encoding.UTF8.GetBytes(json);
                        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
                    }
                }, cancellationToken);

                var buffer = new byte[1024];
                while (ws.State == WebSocketState.Open)
                {
                    var result = await ws.ReceiveAsync(buffer, cancellationToken);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        break;
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
        }).WithName("AppNotificationStream").ExcludeFromDescription();
    }
}
