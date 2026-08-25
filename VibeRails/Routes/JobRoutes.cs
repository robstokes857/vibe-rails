using Serilog;
using VibeRails.DTOs;
using VibeRails.Services;
using VibeRails.Services.Jobs;
using VibeRails.Utils;

namespace VibeRails.Routes;

public static class JobRoutes
{
    public static void Map(WebApplication app, string launchDirectory)
    {
        app.MapGet("/api/v1/jobs", (
            IJobService service,
            string? projectPath,
            CancellationToken cancellationToken) =>
            ExecuteAsync(() => service.GetJobsAsync(projectPath, cancellationToken)))
            .WithName("GetJobs");

        app.MapGet("/api/v1/jobs/{id:long}", (
            IJobService service,
            long id,
            CancellationToken cancellationToken) =>
            ExecuteAsync(() => service.GetJobAsync(id, cancellationToken)))
            .WithName("GetJob");

        app.MapPost("/api/v1/jobs", async (
            IJobService service,
            CreateJobRequest request,
            CancellationToken cancellationToken) =>
            await ExecuteAsync(async () =>
            {
                var projectPath = await ResolveCurrentRepositoryAsync(launchDirectory, cancellationToken);
                return await service.CreateJobAsync(request with { ProjectPath = projectPath }, cancellationToken);
            }))
            .WithName("CreateJob");

        app.MapPut("/api/v1/jobs/{id:long}", async (
            IJobService service,
            long id,
            UpdateJobRequest request,
            CancellationToken cancellationToken) =>
            await ExecuteAsync(async () =>
            {
                var projectPath = await ResolveCurrentRepositoryAsync(launchDirectory, cancellationToken);
                return await service.UpdateJobAsync(id, request with { ProjectPath = projectPath }, cancellationToken);
            }))
            .WithName("UpdateJob");

        app.MapDelete("/api/v1/jobs/{id:long}", async (
            IJobService service,
            long id,
            CancellationToken cancellationToken) =>
        {
            return await ExecuteAsync(async () =>
            {
                await service.DeleteJobAsync(id, cancellationToken);
                return new JobActionResponse(true, "Automation deleted.");
            });
        }).WithName("DeleteJob");

        // Run Now only enqueues and wakes the scheduler. The launch itself belongs to
        // JobLaunchService, which opens the Automation's own native terminal window exactly as a
        // scheduled or retried run does - an Automation never runs inside a Web UI terminal tab.
        app.MapPost("/api/v1/jobs/{id:long}/run", (
            IJobService service,
            long id,
            CancellationToken cancellationToken) =>
            ExecuteAsync(() => service.RunNowAsync(id, cancellationToken)))
            .WithName("RunJobNow");

        app.MapGet("/api/v1/jobs/runs", (
            IJobService service,
            long? jobId,
            int? limit,
            int? page,
            int? pageSize,
            CancellationToken cancellationToken) =>
            ExecuteAsync(() => jobId.HasValue
                ? service.GetRunsPageAsync(jobId.Value, page ?? 1, pageSize ?? limit ?? 50, cancellationToken)
                : service.GetRunsAsync(null, limit ?? 100, cancellationToken)))
            .WithName("GetJobRuns");

        // Registered ahead of /runs/{runId} so the literal segment is the obvious match. ASP.NET
        // already prefers literals over route parameters; the ordering just keeps that visible.
        app.MapGet("/api/v1/jobs/runs/summary", (
            IJobService service,
            string? projectPath,
            CancellationToken cancellationToken) =>
            ExecuteAsync(() => service.GetRunSummariesAsync(projectPath, cancellationToken)))
            .WithName("GetJobRunSummaries");

        app.MapPost("/api/v1/jobs/runs/delete", (
            IJobService service,
            DeleteJobRunsRequest request,
            CancellationToken cancellationToken) =>
            ExecuteAsync(() => service.DeleteRunsAsync(request.RunIds ?? [], cancellationToken)))
            .WithName("DeleteJobRuns");

        app.MapGet("/api/v1/jobs/runs/{runId}", (
            IJobService service,
            string runId,
            CancellationToken cancellationToken) =>
            ExecuteAsync(() => service.GetRunAsync(runId, cancellationToken)))
            .WithName("GetJobRun");

        app.MapDelete("/api/v1/jobs/runs/{runId}", (
            IJobService service,
            string runId,
            CancellationToken cancellationToken) =>
            ExecuteAsync(() => service.DeleteRunsAsync([runId], cancellationToken)))
            .WithName("DeleteJobRun");

        app.MapPost("/api/v1/jobs/runs/{runId}/cancel", (
            IJobService service,
            string runId,
            CancellationToken cancellationToken) =>
            ExecuteAsync(() => service.CancelRunAsync(runId, cancellationToken)))
            .WithName("CancelJobRun");

        app.MapPost("/api/v1/jobs/runs/{runId}/retry", (
            IJobService service,
            string runId,
            CancellationToken cancellationToken) =>
            ExecuteAsync(() => service.RetryRunAsync(runId, cancellationToken)))
            .WithName("RetryJobRun");
    }

    private static async Task<IResult> ExecuteAsync<T>(Func<Task<T>> operation)
    {
        try
        {
            return Results.Ok(await operation());
        }
        catch (JobServiceException ex)
        {
            return Results.Json(new ErrorResponse(ex.Message), statusCode: ex.StatusCode);
        }
        catch (OperationCanceledException)
        {
            // Request aborts are normal (navigation, refresh, host shutdown). Let ASP.NET handle
            // cancellation instead of logging it and fabricating a server-error response.
            throw;
        }
        catch (Exception ex)
        {
            // Anything that is not a JobServiceException used to escape as an unhandled exception,
            // which reached the client as a 500 with an empty body and left no log line to explain
            // it. A store-level fault is exactly the case where the message matters most.
            Log.Error(ex, "[Jobs ✘] Unhandled Automation API failure");
            return Results.Json(
                new ErrorResponse("The Automation request failed. See the VibeRails log for details."),
                statusCode: 500);
        }
    }

    private static async Task<string> ResolveCurrentRepositoryAsync(
        string launchDirectory,
        CancellationToken cancellationToken)
    {
        var configuredRoot = ParserConfigs.GetRootPath();
        if (ParserConfigs.GetIsInGit()
            && !string.IsNullOrWhiteSpace(configuredRoot)
            && Directory.Exists(configuredRoot))
        {
            return configuredRoot;
        }

        var result = await GitProcessRunner.RunAsync(
            ["rev-parse", "--show-toplevel"],
            launchDirectory,
            TimeSpan.FromSeconds(5),
            cancellationToken);
        if (result.TimedOut || result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StdOut))
            throw JobServiceException.BadRequest("Automations require VibeRails to be opened in a Git repository.");

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(result.StdOut.Trim()));
    }
}
