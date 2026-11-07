using VibeRails.DTOs;

namespace VibeRails.Routes;

/// <summary>Compatibility responses never open or mutate retired definition storage.</summary>
public static class RetiredLaunchRoutes
{
    public static void Map(WebApplication app)
    {
        foreach (var path in new[] { "/api/v1/environments/{**rest}", "/api/v1/jobs/{**rest}",
                     "/api/v1/automation-nav/{**rest}", "/api/v1/codex/settings/{**rest}",
                     "/api/v1/claude/settings/{**rest}", "/api/v1/board/cards/{card}/automations/{**rest}",
                     "/api/v1/board/columns/{column}/automation/{**rest}", "/api/v1/cli/launch/{cli}" })
            app.MapMethods(path, ["GET", "POST", "PUT", "PATCH", "DELETE"], () =>
                Results.Json(new ErrorResponse("Custom environments and multi-step Automations are retired. Use the launch catalog to explicitly select an Agent or Action. Legacy data is retained."), statusCode: 410));
    }
}
