using VibeRails.DTOs;
using VibeRails.Services.Integrations.VibeCodeRemote;

namespace VibeRails.Routes;

public static class SwarmRoutes
{
    public static void Map(WebApplication app)
    {
        app.MapPost("/api/v1/swarm/plan", async (SwarmPlanRequest request, SwarmService swarmService, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.TaskDescription))
                return Results.BadRequest(new ErrorResponse("Message is required"));

            var plan = await swarmService.CreatePlanAsync(request, ct);
            return Results.Content(plan, "application/json");
        }).WithName("CreateSwarmPlan");
    }
}
