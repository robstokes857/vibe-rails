using Serilog;
using VibeRails.DTOs;
using VibeRails.Services.Jobs;

namespace VibeRails.Routes;

/// <summary>
/// Authenticated lifecycle surface for the current user's VibeRails Demon registration. This
/// route group is mapped only by an active root backend; child/Environment web hosts return 404.
/// </summary>
public static class JobDaemonRoutes
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/v1/jobs/demon", (
            IJobDaemonLifecycleService service,
            CancellationToken cancellationToken) =>
            ExecuteAsync(() => service.GetStatusAsync(cancellationToken)))
            .WithName("GetJobDemonStatus");

        app.MapPost("/api/v1/jobs/demon/install", (
            IJobDaemonLifecycleService service,
            CancellationToken cancellationToken) =>
            ExecuteAsync(() => service.InstallAsync(cancellationToken)))
            .WithName("InstallJobDemon");

        app.MapPost("/api/v1/jobs/demon/start", (
            IJobDaemonLifecycleService service,
            CancellationToken cancellationToken) =>
            ExecuteAsync(() => service.StartAsync(cancellationToken)))
            .WithName("StartJobDemon");

        app.MapPost("/api/v1/jobs/demon/stop", (
            IJobDaemonLifecycleService service,
            CancellationToken cancellationToken) =>
            ExecuteAsync(() => service.StopAsync(cancellationToken)))
            .WithName("StopJobDemon");

        app.MapPost("/api/v1/jobs/demon/restart", (
            IJobDaemonLifecycleService service,
            CancellationToken cancellationToken) =>
            ExecuteAsync(() => service.RestartAsync(cancellationToken)))
            .WithName("RestartJobDemon");

        app.MapPost("/api/v1/jobs/demon/repair", (
            IJobDaemonLifecycleService service,
            CancellationToken cancellationToken) =>
            ExecuteAsync(() => service.RepairAsync(cancellationToken)))
            .WithName("RepairJobDemon");

        app.MapDelete("/api/v1/jobs/demon", (
            IJobDaemonLifecycleService service,
            CancellationToken cancellationToken) =>
            ExecuteAsync(() => service.UninstallAsync(cancellationToken)))
            .WithName("UninstallJobDemon");
    }

    private static async Task<IResult> ExecuteAsync<T>(Func<Task<T>> operation)
    {
        try
        {
            return Results.Ok(await operation());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[VBD] Unhandled Demon lifecycle API failure");
            return Results.Json(
                new ErrorResponse("The VibeRails Demon request failed. See the VibeRails log for details."),
                statusCode: 500);
        }
    }
}
