using TokenSaver;
using VibeRails.DTOs;
using VibeRails.Services.LlmProxy;
using VibeRails.Services.Terminal;

namespace VibeRails.Routes;

/// <summary>Self-completion on the existing backend listener and its two-credential control gate.</summary>
public static class AgentSessionControlRoutes
{
    public const string Path = "/llm/control/agent/end-session";
    public const string SessionHeader = "X-VibeRails-Agent-Session";

    public static void Map(WebApplication app) => app.MapPost(Path, (RequestDelegate)(async context =>
    {
        if (!context.RequestServices.GetRequiredService<ILlmProxyAuthGate>().IsRequestAuthenticated(context))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
        var sessionId = context.Request.Headers[SessionHeader].ToString();
        if (!Guid.TryParseExact(sessionId, "D", out _))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        var due = context.RequestServices.GetRequiredService<AgentSessionEndScheduler>().Schedule(sessionId);
        if (due is null) { context.Response.StatusCode = StatusCodes.Status409Conflict; return; }
        context.Response.Headers.CacheControl = "no-store";
        await Results.Ok(new AgentSessionEndResponse(due.Value)).ExecuteAsync(context);
    })).WithName("EndOwnAgentSession");
}
