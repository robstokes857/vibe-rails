using System.Text.Json;
using VibeRails.Services.Integrations.VibeCodeRemote;

namespace VibeRails.Routes;

/// <summary>Root-only session sharing. CookieAuthMiddleware requires both process and tab credentials.</summary>
public static class SessionSharingRoutes
{
    // Name plus up to ten addresses of 320 characters fits well inside this bound.
    internal const int MaxBodyBytes = 8192;

    public static void Map(WebApplication app)
    {
        // A newer MCP process can inherit an older root that is still running. The sharing tool asks
        // here before creating a link for listed people; an older root has no such route, so it is
        // never asked to create a link it would silently make public.
        app.MapGet("/api/v1/session-sharing/capabilities", (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Json(new ShareCapabilitiesResponse([ShareAudience.Public, ShareAudience.Email], ShareAudience.RecipientLimit),
                SessionSharingJsonContext.Default.ShareCapabilitiesResponse);
        });

        app.MapPost("/api/v1/sessions/{sessionId:guid}/sharing-links", async (
            Guid sessionId, HttpContext context, SessionSharingService sharing, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            if (context.Items[typeof(VibeRails.Auth.AgentCredentialService.Contact)] is VibeRails.Auth.AgentCredentialService.Contact contact)
            {
                var tabs = await context.RequestServices.GetRequiredService<VibeRails.Services.Terminal.ITerminalTabHostService>().ListTabsAsync(ct);
                var ownerTab = tabs.FirstOrDefault(tab => tab.TabId == contact.Owner && tab.HasActiveSession);
                var ownsSession = contact.Owner == sessionId.ToString("D") || ownerTab?.SessionId == sessionId.ToString("D");
                if (!ownsSession && ownerTab?.JobRunId is { } runId)
                {
                    var actions = await context.RequestServices.GetRequiredService<VibeRails.DB.IJobStore>().GetRunActionsAsync(runId, ct);
                    ownsSession = actions.Any(action => action.SessionId == sessionId.ToString("D"));
                }
                if (!ownsSession)
                    return Results.StatusCode(403);
            }
            if (context.Request.ContentLength > MaxBodyBytes) return Results.StatusCode(413);
            if (!context.Request.HasJsonContentType()) return Results.StatusCode(415);
            CreateSessionShareRequest? body;
            try
            {
                var bytes = await SessionSharingService.ReadBoundedAsync(context.Request.Body, MaxBodyBytes, ct);
                body = JsonSerializer.Deserialize(bytes, SessionSharingJsonContext.Default.CreateSessionShareRequest);
            }
            catch (InvalidDataException) { return Results.StatusCode(413); }
            catch (JsonException) { return Results.StatusCode(400); }
            var result = await sharing.CreateAsync(sessionId, body?.DisplayName, body?.Access, body?.Emails, ct);
            // Upstream authentication errors are domain outcomes: a local 401 would trigger
            // the SPA's process-authentication bootstrap and could repeat the creation POST.
            return Results.Json(result, SessionSharingJsonContext.Default.SessionShareResponse);
        });
    }
}
