using VibeRails.DTOs;
using VibeRails.Services.Integrations.VibeCodeRemote;

namespace VibeRails.Routes;

public static class DebugBundleRoutes
{
    public static void Map(WebApplication app)
    {
        // Build an encrypted debug bundle for one session and upload it to viberails.ai.
        // Always returns 200 with a structured status so the SPA can react (e.g. prompt for
        // an API key) without colliding with the local server's own auth handling of 401.
        app.MapPost("/api/v1/debug-bundle/{sessionId}/send", async (
            IDebugBundleService debugBundleService,
            string sessionId,
            DebugBundleSendRequest? request,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(sessionId))
                return Results.BadRequest(new ErrorResponse("Session id is required."));

            var result = await debugBundleService.SendAsync(sessionId, request?.Message, cancellationToken);

            var status = result.Status switch
            {
                DebugBundleStatus.Success => "ok",
                DebugBundleStatus.NoApiKey => "no_api_key",
                DebugBundleStatus.SessionNotFound => "not_found",
                _ => "failed"
            };
            var message = result.Status switch
            {
                DebugBundleStatus.Success => "Debug log sent. Thank you!",
                DebugBundleStatus.NoApiKey => "Add a valid API key in settings to send a debug log.",
                DebugBundleStatus.SessionNotFound => "Session not found.",
                _ => result.Detail ?? "Failed to send debug log."
            };

            return Results.Ok(new DebugBundleSendResponse(result.Status == DebugBundleStatus.Success, status, message));
        }).WithName("SendDebugBundle");
    }
}
