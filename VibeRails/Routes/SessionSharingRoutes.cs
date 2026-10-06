using System.Text.Json;
using VibeRails.Services.Integrations.VibeCodeRemote;

namespace VibeRails.Routes;

/// <summary>Root-only session sharing. CookieAuthMiddleware requires both process and tab credentials.</summary>
public static class SessionSharingRoutes
{
    public static void Map(WebApplication app)
    {
        app.MapPost("/api/v1/sessions/{sessionId:guid}/sharing-links", async (
            Guid sessionId, HttpContext context, SessionSharingService sharing, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            if (context.Request.ContentLength > 4096) return Results.StatusCode(413);
            if (!context.Request.HasJsonContentType()) return Results.StatusCode(415);
            CreateSessionShareRequest? body;
            try
            {
                var bytes = await SessionSharingService.ReadBoundedAsync(context.Request.Body, 4096, ct);
                body = JsonSerializer.Deserialize(bytes, SessionSharingJsonContext.Default.CreateSessionShareRequest);
            }
            catch (InvalidDataException) { return Results.StatusCode(413); }
            catch (JsonException) { return Results.StatusCode(400); }
            var result = await sharing.CreateAsync(sessionId, body?.DisplayName, ct);
            // Upstream authentication errors are domain outcomes: a local 401 would trigger
            // the SPA's process-authentication bootstrap and could repeat the creation POST.
            return Results.Json(result, SessionSharingJsonContext.Default.SessionShareResponse);
        });
    }
}
