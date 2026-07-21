using VibeRails.DB;
using VibeRails.DTOs;

namespace VibeRails.Services.Jobs;

public interface IJobService
{
    Task<JobListResponse> GetJobsAsync(string? projectPath, CancellationToken cancellationToken = default);
    Task<JobResponse> GetJobAsync(long id, CancellationToken cancellationToken = default);
    Task<JobResponse> CreateJobAsync(CreateJobRequest request, CancellationToken cancellationToken = default);
    Task<JobResponse> UpdateJobAsync(long id, UpdateJobRequest request, CancellationToken cancellationToken = default);
    Task DeleteJobAsync(long id, CancellationToken cancellationToken = default);
    Task<JobActionResponse> RunNowAsync(long id, CancellationToken cancellationToken = default);
    Task<JobRunListResponse> GetRunsAsync(long? jobId, int limit, CancellationToken cancellationToken = default);
    Task<JobRunResponse> GetRunAsync(string runId, CancellationToken cancellationToken = default);
    Task<JobRunLogsResponse> GetRunLogsAsync(string runId, long afterSequence, int limit, CancellationToken cancellationToken = default);
    Task<JobActionResponse> CancelRunAsync(string runId, CancellationToken cancellationToken = default);
    Task<JobActionResponse> RetryRunAsync(string runId, CancellationToken cancellationToken = default);
    Task<JobWorkerStatusResponse> GetWorkerStatusAsync(CancellationToken cancellationToken = default);
    Task<JobActionResponse> RepairWorkerAsync(CancellationToken cancellationToken = default);
    Task<JobActionResponse> DisableWorkerAsync(CancellationToken cancellationToken = default);
}

public sealed class JobService(
    IJobStore store,
    IRepository repository,
    IJobExecutableResolver executableResolver,
    IJobWorkerSupervisor workerSupervisor,
    JobWorkspaceService workspaceService) : IJobService
{
    private const int MaximumNameLength = 100;
    private const int MaximumPromptLength = 50_000;
    private const int MinimumTimeoutMinutes = 1;
    private const int MaximumTimeoutMinutes = 120;

    public async Task<JobListResponse> GetJobsAsync(string? projectPath, CancellationToken cancellationToken = default)
    {
        var jobs = await store.GetJobsAsync(projectPath, cancellationToken: cancellationToken);
        return new JobListResponse(jobs.Select(ToResponse).ToList());
    }

    public async Task<JobResponse> GetJobAsync(long id, CancellationToken cancellationToken = default)
    {
        var job = await store.GetJobAsync(id, cancellationToken)
            ?? throw JobServiceException.NotFound("Job not found.");
        if (job.DeletedUtc is not null)
            throw JobServiceException.NotFound("Job not found.");
        return ToResponse(job);
    }

    public async Task<JobResponse> CreateJobAsync(CreateJobRequest request, CancellationToken cancellationToken = default)
    {
        if (request.EnvironmentId is null)
        {
            throw JobServiceException.BadRequest(
                "Choose an Environment / Worker before creating an automation Job.");
        }

        var normalized = await ValidateAndNormalizeAsync(request, cancellationToken);
        if (normalized.Enabled)
            await EnsureWorkerAvailableAsync(cancellationToken);

        var executable = executableResolver.Resolve(normalized.Llm);
        var created = await store.CreateJobAsync(normalized, executable, cancellationToken);
        return ToResponse(created);
    }

    public async Task<JobResponse> UpdateJobAsync(long id, UpdateJobRequest request, CancellationToken cancellationToken = default)
    {
        var existing = await store.GetJobAsync(id, cancellationToken);
        if (existing is null || existing.DeletedUtc is not null)
            throw JobServiceException.NotFound("Job not found.");
        if (existing.EnvironmentId is not null && request.EnvironmentId is null)
        {
            throw JobServiceException.BadRequest(
                "A worker-backed Job cannot be detached from its Environment / Worker. Choose another worker instead.");
        }

        var normalized = await ValidateAndNormalizeAsync(request, cancellationToken);
        if (normalized.Enabled)
            await EnsureWorkerAvailableAsync(cancellationToken);

        var executable = executableResolver.Resolve(normalized.Llm);
        var updated = await store.UpdateJobAsync(id, normalized, executable, cancellationToken)
            ?? throw JobServiceException.NotFound("Job not found.");
        return ToResponse(updated);
    }

    public async Task DeleteJobAsync(long id, CancellationToken cancellationToken = default)
    {
        if (!await store.SoftDeleteJobAsync(id, cancellationToken))
            throw JobServiceException.NotFound("Job not found.");
    }

    public async Task<JobActionResponse> RunNowAsync(long id, CancellationToken cancellationToken = default)
    {
        var job = await store.GetJobAsync(id, cancellationToken);
        if (job is null || job.DeletedUtc is not null)
            throw JobServiceException.NotFound("Job not found.");
        if (executableResolver.Resolve(job.Llm) is null && string.IsNullOrWhiteSpace(job.ExecutablePath))
            throw JobServiceException.BadRequest($"The {job.Llm} CLI is not available on PATH.");

        await EnsureWorkerAvailableAsync(cancellationToken);
        var runId = await store.EnqueueManualRunAsync(id, cancellationToken: cancellationToken)
            ?? throw JobServiceException.Conflict("The job could not be queued.");
        return new JobActionResponse(true, "Job queued.", runId);
    }

    public async Task<JobRunListResponse> GetRunsAsync(
        long? jobId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var runs = await store.GetRunsAsync(jobId, limit, cancellationToken);
        return new JobRunListResponse(runs.Select(ToResponse).ToList());
    }

    public async Task<JobRunResponse> GetRunAsync(string runId, CancellationToken cancellationToken = default)
    {
        var run = await store.GetRunAsync(runId, cancellationToken)
            ?? throw JobServiceException.NotFound("Job run not found.");
        return ToResponse(run);
    }

    public async Task<JobRunLogsResponse> GetRunLogsAsync(
        string runId,
        long afterSequence,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (await store.GetRunAsync(runId, cancellationToken) is null)
            throw JobServiceException.NotFound("Job run not found.");
        var logs = await store.GetRunLogsAsync(runId, afterSequence, limit, cancellationToken);
        return new JobRunLogsResponse(runId, logs.ToList(), logs.Count == 0 ? afterSequence : logs[^1].Sequence);
    }

    public async Task<JobActionResponse> CancelRunAsync(string runId, CancellationToken cancellationToken = default)
    {
        if (!await store.RequestCancelAsync(runId, cancellationToken))
            throw JobServiceException.Conflict("Only queued or running jobs can be cancelled.");
        return new JobActionResponse(true, "Cancellation requested.", runId);
    }

    public async Task<JobActionResponse> RetryRunAsync(string runId, CancellationToken cancellationToken = default)
    {
        var source = await store.GetRunAsync(runId, cancellationToken)
            ?? throw JobServiceException.NotFound("Job run not found.");
        if (source.Status is not (JobRunStatus.Succeeded
            or JobRunStatus.Failed
            or JobRunStatus.Cancelled
            or JobRunStatus.TimedOut
            or JobRunStatus.Interrupted))
        {
            throw JobServiceException.Conflict("Only completed runs for active jobs can be retried.");
        }
        if (executableResolver.Resolve(source.Llm) is null && string.IsNullOrWhiteSpace(source.ExecutablePath))
            throw JobServiceException.BadRequest($"The {source.Llm} CLI is not available on PATH.");

        await EnsureWorkerAvailableAsync(cancellationToken);
        JobWorkspaceService.CapturedStagedSnapshot? capturedSnapshot = null;
        var queuePublicationStarted = false;
        try
        {
            try
            {
                capturedSnapshot = await workspaceService.CaptureRetrySnapshotAsync(source, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw JobServiceException.Conflict(
                    $"The staged snapshot can no longer be recreated; no retry was queued. {ex.Message}");
            }

            var contextOverride = capturedSnapshot is null
                ? null
                : JobWorkspaceService.CreateRetryContextJson(source.TriggerContextJson, capturedSnapshot);
            queuePublicationStarted = true;
            var retryId = await store.EnqueueRetryAsync(runId, contextOverride, cancellationToken);
            if (retryId is null)
            {
                if (capturedSnapshot is not null)
                    workspaceService.DiscardCapturedSnapshot(capturedSnapshot);
                throw JobServiceException.Conflict("Only completed runs for active jobs can be retried.");
            }

            if (capturedSnapshot is not null)
            {
                await workspaceService.MaterializeRunSnapshotsAsync(
                    capturedSnapshot,
                    [retryId],
                    cancellationToken);
            }
            return new JobActionResponse(true, "Job retry queued.", retryId);
        }
        finally
        {
            if (capturedSnapshot is not null)
            {
                if (!queuePublicationStarted)
                    workspaceService.DiscardCapturedSnapshot(capturedSnapshot);
                capturedSnapshot.Dispose();
            }
        }
    }

    public async Task<JobWorkerStatusResponse> GetWorkerStatusAsync(CancellationToken cancellationToken = default)
    {
        var status = await workerSupervisor.GetStatusAsync(cancellationToken);
        var legacy = await store.GetLegacyCronCountsAsync(cancellationToken);
        if (legacy.Definitions == 0 && legacy.Runs == 0)
            return status;

        return status with
        {
            Message = $"{status.Message} Legacy removed cron data was found ({legacy.Definitions} definitions, {legacy.Runs} runs); Jobs does not reuse it."
        };
    }

    public async Task<JobActionResponse> RepairWorkerAsync(CancellationToken cancellationToken = default)
    {
        await workerSupervisor.RepairAsync(cancellationToken);
        return new JobActionResponse(true, "Jobs worker registration repaired.");
    }

    public async Task<JobActionResponse> DisableWorkerAsync(CancellationToken cancellationToken = default)
    {
        await workerSupervisor.DisableAsync(cancellationToken);
        return new JobActionResponse(true, "Jobs worker disabled. Enabled jobs remain saved but will not run until the worker is repaired.");
    }

    private async Task<CreateJobRequest> ValidateAndNormalizeAsync(
        CreateJobRequest request,
        CancellationToken cancellationToken)
    {
        var projectPath = await ValidateProjectAsync(request.ProjectPath, cancellationToken);
        var worker = await ValidateCommonAsync(
            request.Name, request.Llm, request.EnvironmentId, request.Prompt,
            request.ExecutionMode, request.TimeoutMinutes, request.Enabled,
            request.Triggers, requireEnvironmentPrompt: true, cancellationToken);
        return request with
        {
            Name = request.Name.Trim(),
            ProjectPath = projectPath,
            Llm = worker.Llm,
            Prompt = worker.Prompt,
            Triggers = request.Triggers?.ToList() ?? []
        };
    }

    private async Task<UpdateJobRequest> ValidateAndNormalizeAsync(
        UpdateJobRequest request,
        CancellationToken cancellationToken)
    {
        var projectPath = await ValidateProjectAsync(request.ProjectPath, cancellationToken);
        var worker = await ValidateCommonAsync(
            request.Name, request.Llm, request.EnvironmentId, request.Prompt,
            request.ExecutionMode, request.TimeoutMinutes, request.Enabled,
            request.Triggers, requireEnvironmentPrompt: request.EnvironmentId is not null, cancellationToken);
        return request with
        {
            Name = request.Name.Trim(),
            ProjectPath = projectPath,
            Llm = worker.Llm,
            Prompt = worker.Prompt,
            Triggers = request.Triggers?.ToList() ?? []
        };
    }

    private async Task<(LLM Llm, string Prompt)> ValidateCommonAsync(
        string name,
        LLM llm,
        int? environmentId,
        string prompt,
        JobExecutionMode executionMode,
        int timeoutMinutes,
        bool enabled,
        IReadOnlyList<JobTriggerRequest>? triggers,
        bool requireEnvironmentPrompt,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > MaximumNameLength)
            throw JobServiceException.BadRequest($"Job name is required and must be at most {MaximumNameLength} characters.");

        // Environment-backed Jobs use the shared Environment / Worker as their source of truth.
        // Keep accepting the duplicated wire fields for legacy clients and base-CLI Jobs, but do
        // not let a caller run a saved worker with a different provider or initial message.
        var resolvedLlm = llm;
        var resolvedPrompt = prompt?.Trim() ?? string.Empty;
        if (environmentId is not null)
        {
            var environment = (await repository.GetAllEnvironmentsAsync(cancellationToken))
                .FirstOrDefault(item => item.Id == environmentId.Value);
            if (environment is null)
                throw JobServiceException.BadRequest("The selected environment / worker no longer exists.");

            resolvedLlm = environment.LLM;
            if (!string.IsNullOrWhiteSpace(environment.CustomPrompt))
            {
                resolvedPrompt = environment.CustomPrompt.Trim();
            }
            else if (requireEnvironmentPrompt)
            {
                throw JobServiceException.BadRequest(
                    "The selected environment / worker needs an Initial Message before it can run as a Job.");
            }
        }

        if (resolvedLlm is not (LLM.Codex
            or LLM.Claude
            or LLM.Antigravity
            or LLM.Copilot
            or LLM.OpenCode
            or LLM.Glm52
            or LLM.KimiK3))
        {
            throw JobServiceException.BadRequest("The selected LLM cannot run as an automated Job.");
        }
        if (string.IsNullOrWhiteSpace(resolvedPrompt) || resolvedPrompt.Length > MaximumPromptLength)
            throw JobServiceException.BadRequest($"Initial message is required and must be at most {MaximumPromptLength} characters.");
        if (!Enum.IsDefined(executionMode))
            throw JobServiceException.BadRequest("Unknown execution mode.");
        if (timeoutMinutes is < MinimumTimeoutMinutes or > MaximumTimeoutMinutes)
            throw JobServiceException.BadRequest($"Timeout must be {MinimumTimeoutMinutes}–{MaximumTimeoutMinutes} minutes.");

        var triggerList = triggers ?? [];
        var duplicate = triggerList.GroupBy(trigger => trigger.Kind).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw JobServiceException.BadRequest($"Only one {duplicate.Key} trigger may be configured per job.");
        foreach (var trigger in triggerList)
        {
            if (trigger.Kind == JobTriggerKind.Manual)
                throw JobServiceException.BadRequest("Run Now is always available and is not stored as a trigger.");
            if (trigger.Kind is not (JobTriggerKind.Schedule or JobTriggerKind.Vca or JobTriggerKind.Commit))
                throw JobServiceException.BadRequest("Unknown trigger kind.");
            var error = JobScheduleCalculator.Validate(trigger);
            if (error is not null)
                throw JobServiceException.BadRequest(error);
        }

        if (enabled && executableResolver.Resolve(resolvedLlm) is null)
            throw JobServiceException.BadRequest($"The {resolvedLlm} CLI was not found. Install it or add it to PATH before enabling this job.");

        return (resolvedLlm, resolvedPrompt);
    }

    private static async Task<string> ValidateProjectAsync(string projectPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
            throw JobServiceException.BadRequest("Project path is required.");

        string fullPath;
        try
        {
            fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectPath.Trim()));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw JobServiceException.BadRequest("Project path is invalid.");
        }
        if (!Directory.Exists(fullPath))
            throw JobServiceException.BadRequest("Project directory does not exist.");

        var result = await GitProcessRunner.RunAsync(
            ["rev-parse", "--show-toplevel"],
            fullPath,
            TimeSpan.FromSeconds(5),
            cancellationToken);
        if (result.TimedOut || result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StdOut))
            throw JobServiceException.BadRequest("Jobs must target a Git repository root.");

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(result.StdOut.Trim()));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!root.Equals(fullPath, comparison))
            throw JobServiceException.BadRequest($"Project path must be the Git repository root: {root}");
        return root;
    }

    private async Task EnsureWorkerAvailableAsync(CancellationToken cancellationToken)
    {
        try
        {
            await workerSupervisor.EnsureInstalledAndRunningAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new JobServiceException(500, $"Could not enable the Jobs worker: {ex.Message}");
        }
    }

    private static JobResponse ToResponse(JobDefinitionRecord job) => new(
        job.Id, job.Name, job.ProjectPath, job.Llm, job.EnvironmentId, job.EnvironmentName,
        job.Prompt, job.ExecutionMode, job.TimeoutMinutes, job.Enabled, job.ExecutablePath,
        job.CreatedUtc, job.UpdatedUtc, job.DeletedUtc, job.Triggers.ToList());

    private static JobRunResponse ToResponse(JobRunRecord run) => new(
        run.Id, run.JobId, run.JobName, run.TriggerKind, run.Status, run.ProjectPath, run.Llm,
        run.EnvironmentName, run.ExecutionMode, run.QueuedUtc, run.StartedUtc, run.EndedUtc,
        run.ExitCode, run.ResultText, run.ErrorMessage, run.WorkspacePath, run.CancelRequested);
}

public sealed class JobServiceException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;

    public static JobServiceException BadRequest(string message) => new(400, message);
    public static JobServiceException NotFound(string message) => new(404, message);
    public static JobServiceException Conflict(string message) => new(409, message);
}
