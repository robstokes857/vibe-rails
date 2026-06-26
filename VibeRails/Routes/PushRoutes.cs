using VibeRails.DTOs;
using VibeRails.Services.Integrations.VibeCodeRemote;

namespace VibeRails.Routes;

public static class PushRoutes
{
    public static void Map(WebApplication app)
    {
        // POST /api/v1/push/send - Local proxy: forwards a tab status push to the VibeRails-Front
        // web-push API (keeps the X-Api-Key server-side). The browser treats this as
        // fire-and-forget; the handler still awaits the upstream send so the request scope (and the
        // typed HttpClient) stays alive for the call. Returns a small JSON body so the client's
        // JSON-parsing apiCall() doesn't throw on an empty 200.
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

            return Results.Ok(new MessageResponse("ok"));
        }).WithName("PushSend");
    }
}
