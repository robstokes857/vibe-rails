using VibeRails.DTOs;
using VibeRails.Interfaces;

namespace VibeRails.Routes;

public static class ClaudePlanRoutes
{
    public static void Map(WebApplication app)
    {
        // GET /api/v1/plans/session/{sessionId} - Get plans for a session
        app.MapGet("/api/v1/plans/session/{sessionId}", async (
            IDbService dbService,
            string sessionId,
            CancellationToken cancellationToken) =>
        {
            var plans = await dbService.GetClaudePlansForSessionAsync(sessionId, cancellationToken);
            return Results.Ok(new ClaudePlanListResponse(plans, plans.Count));
        }).WithName("GetSessionPlans");

        // GET /api/v1/plans/recent - Get recent plans across all sessions
        app.MapGet("/api/v1/plans/recent", async (
            IDbService dbService,
            int? limit,
            CancellationToken cancellationToken) =>
        {
            var plans = await dbService.GetRecentClaudePlansAsync(limit ?? 20, cancellationToken);
            return Results.Ok(new ClaudePlanListResponse(plans, plans.Count));
        }).WithName("GetRecentPlans");

        // GET /api/v1/plans/{planId} - Get a single plan
        app.MapGet("/api/v1/plans/{planId}", async (
            IDbService dbService,
            long planId,
            CancellationToken cancellationToken) =>
        {
            var plan = await dbService.GetClaudePlanAsync(planId, cancellationToken);
            if (plan == null)
            {
                return Results.NotFound(new ErrorResponse($"Plan not found: {planId}"));
            }
            return Results.Ok(plan);
        }).WithName("GetPlan");

        // POST /api/v1/plans - Create a new plan
        app.MapPost("/api/v1/plans", async (
            IDbService dbService,
            CreateClaudePlanRequest request,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrEmpty(request.SessionId))
            {
                return Results.BadRequest(new ErrorResponse("SessionId is required"));
            }

            if (string.IsNullOrEmpty(request.PlanContent))
            {
                return Results.BadRequest(new ErrorResponse("PlanContent is required"));
            }

            var planId = await dbService.CreateClaudePlanAsync(
                request.SessionId,
                request.UserInputId,
                request.PlanFilePath,
                request.PlanContent,
                request.PlanSummary);

            var plan = await dbService.GetClaudePlanAsync(planId, cancellationToken);
            return Results.Ok(plan);
        }).WithName("CreatePlan");

        // PUT /api/v1/plans/{planId}/status - Update plan status
        app.MapPut("/api/v1/plans/{planId}/status", async (
            IDbService dbService,
            long planId,
            UpdateClaudePlanStatusRequest request,
            CancellationToken cancellationToken) =>
        {
            var plan = await dbService.GetClaudePlanAsync(planId, cancellationToken);
            if (plan == null)
            {
                return Results.NotFound(new ErrorResponse($"Plan not found: {planId}"));
            }

            if (string.IsNullOrEmpty(request.Status))
            {
                return Results.BadRequest(new ErrorResponse("Status is required"));
            }

            await dbService.UpdateClaudePlanStatusAsync(planId, request.Status);
            return Results.Ok(new OK("Status updated"));
        }).WithName("UpdatePlanStatus");

        // POST /api/v1/plans/{planId}/complete - Mark plan as completed
        app.MapPost("/api/v1/plans/{planId}/complete", async (
            IDbService dbService,
            long planId,
            CancellationToken cancellationToken) =>
        {
            var plan = await dbService.GetClaudePlanAsync(planId, cancellationToken);
            if (plan == null)
            {
                return Results.NotFound(new ErrorResponse($"Plan not found: {planId}"));
            }

            await dbService.CompleteClaudePlanAsync(planId);
            var updatedPlan = await dbService.GetClaudePlanAsync(planId, cancellationToken);
            return Results.Ok(updatedPlan);
        }).WithName("CompletePlan");
    }
}
