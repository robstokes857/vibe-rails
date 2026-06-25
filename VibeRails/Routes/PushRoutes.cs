using VibeRails.DTOs;
using VibeRails.Services.Integrations.VibeCodeRemote;

namespace VibeRails.Routes;

public static class PushRoutes
{
    public static void Map(WebApplication app)
    {
        // POST /api/v1/push/send - Local proxy: forwards a tab status push to the
        // VibeRails-Front web-push API (keeps the X-Api-Key server-side). Fire-and-forget.
        app.MapPost("/api/v1/push/send", async (
            IPushNotificationService push,
            PushSendRequest? request,
            CancellationToken cancellationToken) =>
        {
            if (request is null || string.IsNullOrWhiteSpace(request.Title))
            {
                return Results.BadRequest(new ErrorResponse("title is required"));
            }

            await push.SendAsync(
                request.Title,
                request.Body ?? string.Empty,
                request.Tag,
                request.ImageBase64,
                cancellationToken);

            return Results.Ok();
        }).WithName("PushSend");
    }
}
