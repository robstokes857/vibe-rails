using VibeRails.DTOs;
using VibeRails.Services;

namespace VibeRails.Routes;

public static class LifecycleRoutes
{
    private static readonly TimeSpan BrowserPulseTtl = TimeSpan.FromSeconds(45);

    public static void Map(WebApplication app)
    {
        app.MapPost("/api/v1/lifecycle/ping", (
            HttpContext context,
            ILocalClientTracker localClientTracker) =>
        {
            var ownerId = GetBrowserOwnerId(context);
            if (ownerId == null)
            {
                return Results.BadRequest(new ErrorResponse("clientId is required"));
            }

            localClientTracker.PulseOwner(ownerId, BrowserPulseTtl);
            return Results.NoContent();
        }).WithName("LifecyclePing");

        app.MapPost("/api/v1/lifecycle/disconnect", (
            HttpContext context,
            ILocalClientTracker localClientTracker) =>
        {
            var ownerId = GetBrowserOwnerId(context);
            if (ownerId == null)
            {
                return Results.BadRequest(new ErrorResponse("clientId is required"));
            }

            localClientTracker.ReleaseOwner(ownerId);
            return Results.NoContent();
        }).WithName("LifecycleDisconnect");

        app.MapPost("/api/v1/shutdown", (IHostApplicationLifetime hostApplicationLifetime) =>
        {
            // Delay stop very slightly so the HTTP response can be flushed.
            _ = Task.Run(async () =>
            {
                await Task.Delay(50);
                hostApplicationLifetime.StopApplication();
            });

            return Results.Ok(new MessageResponse("Shutdown requested"));
        }).WithName("ShutdownHost");
    }

    private static string? GetBrowserOwnerId(HttpContext context)
    {
        var raw = context.Request.Query["clientId"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var trimmed = raw.Trim();
        if (trimmed.Length == 0 || trimmed.Length > 128)
        {
            return null;
        }

        return $"browser:{trimmed}";
    }
}
