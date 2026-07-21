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
                return new JobActionResponse(true, "Job deleted.");
            });
        }).WithName("DeleteJob");

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
            CancellationToken cancellationToken) =>
            ExecuteAsync(() => service.GetRunsAsync(jobId, limit ?? 100, cancellationToken)))
            .WithName("GetJobRuns");

        app.MapGet("/api/v1/jobs/runs/{runId}", (
            IJobService service,
            string runId,
            CancellationToken cancellationToken) =>
            ExecuteAsync(() => service.GetRunAsync(runId, cancellationToken)))
            .WithName("GetJobRun");

        app.MapGet("/api/v1/jobs/runs/{runId}/logs", (
            IJobService service,
            string runId,
            long? afterSequence,
            int? limit,
            CancellationToken cancellationToken) =>
            ExecuteAsync(() => service.GetRunLogsAsync(runId, afterSequence ?? 0, limit ?? 1000, cancellationToken)))
            .WithName("GetJobRunLogs");

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

        app.MapGet("/api/v1/jobs/worker", (
            IJobService service,
            CancellationToken cancellationToken) =>
            ExecuteAsync(() => service.GetWorkerStatusAsync(cancellationToken)))
            .WithName("GetJobsWorkerStatus");

        app.MapPost("/api/v1/jobs/worker/repair", (
            IJobService service,
            CancellationToken cancellationToken) =>
            ExecuteAsync(() => service.RepairWorkerAsync(cancellationToken)))
            .WithName("RepairJobsWorker");

        app.MapDelete("/api/v1/jobs/worker", (
            IJobService service,
            CancellationToken cancellationToken) =>
            ExecuteAsync(() => service.DisableWorkerAsync(cancellationToken)))
            .WithName("DisableJobsWorker");
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
            throw JobServiceException.BadRequest("Jobs require VibeRails to be opened in a Git repository.");

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(result.StdOut.Trim()));
    }
}
