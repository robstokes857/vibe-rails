using VibeRails.DTOs;
using VibeRails.Services.Board;
using VibeRails.Services.Launches;
using VibeRails.Launches;
using VibeRails.Services.Jobs;

namespace VibeRails.Routes;

public static class BoardLaunchRoutes
{
    public static void Map(WebApplication app)
    {
        var api = app.MapGroup("/api/v1/board");
        api.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            try { return await next(context); }
            catch (Exception ex) when (ex is BoardConflictException or LaunchConflictException)
            { return Results.Conflict(new ErrorResponse(ex.Message)); }
            catch (Exception ex) when (ex is BoardValidationException or LaunchValidationException or AutomationScriptValidationException or ArgumentException)
            { return Results.BadRequest(new ErrorResponse(ex.Message)); }
        });
        api.MapGet("/columns/{column}/launches", async (string column, IBoardStore store, IBoardProjectResolver projects, CancellationToken ct) =>
            await store.GetLaunchLaneAsync(await projects.ResolveAsync(ct), column, ct) is { } lane ? Results.Ok(lane) : Results.NotFound());
        api.MapPut("/columns/{column}/launches", async (string column, BoardLaunchLane request, BoardLaunchWorkflow workflow, IBoardProjectResolver projects, CancellationToken ct) =>
            await workflow.SaveAsync(await projects.ResolveAsync(ct), column, request, ct) is { } lane ? Results.Ok(lane) : Results.NotFound());
        api.MapGet("/columns/{column}/launches/activity", async (string column, BoardLaunchWorkflow workflow, IBoardProjectResolver projects, CancellationToken ct) =>
            await workflow.GetLaneActivityAsync(await projects.ResolveAsync(ct), column, ct) is { } activity
                ? Results.Ok(activity) : Results.NotFound());
        api.MapGet("/cards/{card}/launches", async (string card, IBoardStore store, IBoardProjectResolver projects, CancellationToken ct) =>
        {
            var project = await projects.ResolveAsync(ct);
            var row = await store.FindCardAsync(project, card, ct);
            return row is null ? Results.NotFound() : Results.Ok(await store.GetLaunchEventsAsync(project, row.Id, ct));
        });
        api.MapPost("/cards/{card}/launches", async (string card, LaunchRequest request, BoardLaunchWorkflow workflow, IBoardProjectResolver projects, CancellationToken ct) =>
            await workflow.EnqueueCardAsync(await projects.ResolveAsync(ct), card, request, ct) is { } run
                ? Results.Ok(run) : Results.NotFound());
        api.MapPost("/cards/{card}/launches/{entry}/skip", async (string card, string entry, IBoardStore store, IBoardProjectResolver projects, CancellationToken ct) =>
        {
            var project = await projects.ResolveAsync(ct);
            var row = await store.FindCardAsync(project, card, ct);
            return row is not null && await store.ReportLaunchEventAsync(project, row.Id, entry, "skipped", "User skipped this lane entry.", null, null, true, ct)
                ? Results.NoContent() : Results.NotFound();
        });
    }
}
