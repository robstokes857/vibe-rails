using VibeRails.Services.Integrations.VibeCodeRemote;

namespace VibeRails.Routes;

/// <summary>Root-dashboard account linking behind the normal session and tab credentials.</summary>
public static class RemoteAccountLinkRoutes
{
    /// <summary>Maps start, progress and cancellation without a local callback or listener.</summary>
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/v1/settings/remote-link");
        group.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return await next(context);
        });
        group.MapPost("", async (RemoteAccountLinkService service) => Results.Ok(await service.StartAsync()));
        group.MapGet("", async (RemoteAccountLinkService service) => Results.Ok(await service.PollAsync()));
        group.MapDelete("", async (RemoteAccountLinkService service) => Results.Ok(await service.CancelAsync()));
    }
}
