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
    Task<JobActionResponse> CancelRunAsync(string runId, CancellationToken cancellationToken = default);
    Task<JobActionResponse> RetryRunAsync(string runId, CancellationToken cancellationToken = default);
}

public sealed class JobService(
    IJobStore store,
    IRepository repository,
    IJobExecutableResolver executableResolver,
    IJobScheduler scheduler) : IJobService
{
    private const int MaximumNameLength = 100;
    private const int MaximumPromptLength = 50_000;
    private const int MinimumTimeoutMinutes = 1;

    /// <summary>
    /// 12 hours. Deliberately generous, and raised from 2 hours when timeouts became opt-in: this is
    /// no longer the normal way a run ends, it is the backstop for one that has stopped making
    /// progress. An agent working through a large refactor or a long review can legitimately run for
    /// hours, and a limit that cuts those off mid-edit is worse than one that rarely fires. The
    /// bound that matters for resource use is the concurrent-terminal cap, not this.
    /// </summary>
    private const int MaximumTimeoutMinutes = 720;

    public async Task<JobListResponse> GetJobsAsync(string? projectPath, CancellationToken cancellationToken = default)
    {
        var jobs = await store.GetJobsAsync(projectPath, cancellationToken: cancellationToken);
        return new JobListResponse(jobs.Select(ToResponse).ToList());
    }

    public async Task<JobResponse> GetJobAsync(long id, CancellationToken cancellationToken = default)
    {
        var job = await store.GetJobAsync(id, cancellationToken)
            ?? throw JobServiceException.NotFound("Automation not found.");
        if (job.DeletedUtc is not null)
            throw JobServiceException.NotFound("Automation not found.");
        return ToResponse(job);
    }

    public async Task<JobResponse> CreateJobAsync(CreateJobRequest request, CancellationToken cancellationToken = default)
    {
        if (request.EnvironmentId is null)
            throw JobServiceException.BadRequest("Choose an Environment before creating an Automation.");

        var normalized = await ValidateAndNormalizeAsync(request, requireEnvironmentPrompt: true, cancellationToken);
        var created = await store.CreateJobAsync(normalized, cancellationToken);
        return ToResponse(created);
    }

    public async Task<JobResponse> UpdateJobAsync(long id, UpdateJobRequest request, CancellationToken cancellationToken = default)
    {
        var existing = await store.GetJobAsync(id, cancellationToken);
        if (existing is null || existing.DeletedUtc is not null)
            throw JobServiceException.NotFound("Automation not found.");
        if (existing.EnvironmentId is not null && request.EnvironmentId is null)
            throw JobServiceException.BadRequest("An Automation cannot be detached from its Environment. Choose another Environment instead.");

        var normalized = await ValidateAndNormalizeAsync(request, requireEnvironmentPrompt: request.EnvironmentId is not null, cancellationToken);
        var updated = await store.UpdateJobAsync(id, normalized, cancellationToken)
            ?? throw JobServiceException.NotFound("Automation not found.");
        return ToResponse(updated);
    }

    public async Task DeleteJobAsync(long id, CancellationToken cancellationToken = default)
    {
        if (!await store.SoftDeleteJobAsync(id, cancellationToken))
            throw JobServiceException.NotFound("Automation not found.");
    }

    public async Task<JobActionResponse> RunNowAsync(long id, CancellationToken cancellationToken = default)
    {
        var job = await store.GetJobAsync(id, cancellationToken);
        if (job is null || job.DeletedUtc is not null)
            throw JobServiceException.NotFound("Automation not found.");
        if (executableResolver.Resolve(job.Llm) is null)
            throw JobServiceException.BadRequest($"The {job.Llm} CLI is not available on PATH.");

        var runId = await store.EnqueueManualRunAsync(id, cancellationToken)
            ?? throw JobServiceException.Conflict("The Automation could not be queued.");
        scheduler.Kick();
        return new JobActionResponse(true, "Automation queued.", runId);
    }

    public async Task<JobRunListResponse> GetRunsAsync(long? jobId, int limit, CancellationToken cancellationToken = default)
    {
        var runs = await store.GetRunsAsync(jobId, limit, cancellationToken);
        return new JobRunListResponse(runs.Select(ToResponse).ToList());
    }

    public async Task<JobRunResponse> GetRunAsync(string runId, CancellationToken cancellationToken = default)
    {
        var run = await store.GetRunAsync(runId, cancellationToken)
            ?? throw JobServiceException.NotFound("Automation run not found.");
        return ToResponse(run);
    }

    public async Task<JobActionResponse> CancelRunAsync(string runId, CancellationToken cancellationToken = default)
    {
        if (!await store.RequestCancelAsync(runId, cancellationToken))
            throw JobServiceException.Conflict("Only queued or running Automation runs can be cancelled.");
        return new JobActionResponse(true, "Cancellation requested.", runId);
    }

    public async Task<JobActionResponse> RetryRunAsync(string runId, CancellationToken cancellationToken = default)
    {
        var source = await store.GetRunAsync(runId, cancellationToken)
            ?? throw JobServiceException.NotFound("Automation run not found.");
        if (source.Status is not (JobRunStatus.Succeeded
            or JobRunStatus.Failed
            or JobRunStatus.Cancelled
            or JobRunStatus.TimedOut
            or JobRunStatus.Interrupted))
        {
            throw JobServiceException.Conflict("Only completed runs for active Automations can be retried.");
        }
        if (executableResolver.Resolve(source.Llm) is null)
            throw JobServiceException.BadRequest($"The {source.Llm} CLI is not available on PATH.");

        var retryId = await store.EnqueueRetryAsync(runId, cancellationToken)
            ?? throw JobServiceException.Conflict("Only completed runs for active Automations can be retried.");
        scheduler.Kick();
        return new JobActionResponse(true, "Automation retry queued.", retryId);
    }

    private async Task<CreateJobRequest> ValidateAndNormalizeAsync(CreateJobRequest request, bool requireEnvironmentPrompt, CancellationToken cancellationToken)
    {
        var projectPath = await ValidateProjectAsync(request.ProjectPath, cancellationToken);
        var (llm, prompt) = await ValidateCommonAsync(
            request.Name, request.EnvironmentId, request.TimeoutMinutes, request.Triggers,
            requireEnvironmentPrompt, cancellationToken);
        return request with
        {
            Name = request.Name.Trim(),
            ProjectPath = projectPath,
            Llm = llm,
            Prompt = prompt,
            Triggers = request.Triggers?.ToList() ?? []
        };
    }

    private async Task<UpdateJobRequest> ValidateAndNormalizeAsync(UpdateJobRequest request, bool requireEnvironmentPrompt, CancellationToken cancellationToken)
    {
        var projectPath = await ValidateProjectAsync(request.ProjectPath, cancellationToken);
        var (llm, prompt) = await ValidateCommonAsync(
            request.Name, request.EnvironmentId, request.TimeoutMinutes, request.Triggers,
            requireEnvironmentPrompt, cancellationToken);
        return request with
        {
            Name = request.Name.Trim(),
            ProjectPath = projectPath,
            Llm = llm,
            Prompt = prompt,
            Triggers = request.Triggers?.ToList() ?? []
        };
    }

    private async Task<(LLM Llm, string Prompt)> ValidateCommonAsync(
        string name,
        int? environmentId,
        int? timeoutMinutes,
        IReadOnlyList<JobTriggerRequest>? triggers,
        bool requireEnvironmentPrompt,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > MaximumNameLength)
            throw JobServiceException.BadRequest($"Automation name is required and must be at most {MaximumNameLength} characters.");

        // The Environment is the single source of truth for the CLI, model, args, and initial
        // message; the Automation only owns when it runs and its timeout.
        LLM resolvedLlm;
        var resolvedPrompt = string.Empty;
        if (environmentId is null)
            throw JobServiceException.BadRequest("Choose an Environment.");

        var environment = (await repository.GetAllEnvironmentsAsync(cancellationToken))
            .FirstOrDefault(item => item.Id == environmentId.Value)
            ?? throw JobServiceException.BadRequest("The selected Environment no longer exists.");

        resolvedLlm = environment.LLM;
        if (!string.IsNullOrWhiteSpace(environment.CustomPrompt))
            resolvedPrompt = environment.CustomPrompt.Trim();
        else if (requireEnvironmentPrompt)
            throw JobServiceException.BadRequest("The selected Environment needs an Initial Message before it can run as an Automation.");

        if (resolvedLlm is not (LLM.Codex or LLM.Claude or LLM.Antigravity or LLM.Copilot or LLM.OpenCode or LLM.Glm52 or LLM.KimiK3))
            throw JobServiceException.BadRequest("The selected LLM cannot run as an Automation.");
        if (resolvedPrompt.Length > MaximumPromptLength)
            throw JobServiceException.BadRequest($"Initial message must be at most {MaximumPromptLength} characters.");
        // A timeout is optional: with none set the run lives until its CLI exits or the user closes
        // its window. Only validate the range when the user actually opted in.
        if (timeoutMinutes is not null && timeoutMinutes is < MinimumTimeoutMinutes or > MaximumTimeoutMinutes)
            throw JobServiceException.BadRequest($"Timeout must be {MinimumTimeoutMinutes}–{MaximumTimeoutMinutes} minutes, or left off entirely.");

        var triggerList = triggers ?? [];
        var duplicate = triggerList.GroupBy(trigger => trigger.Kind).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw JobServiceException.BadRequest($"Only one {duplicate.Key} trigger may be configured per Automation.");
        foreach (var trigger in triggerList)
        {
            if (trigger.Kind == JobTriggerKind.Manual)
                throw JobServiceException.BadRequest("Run Now is always available and is not stored as a trigger.");
            if (trigger.Kind is not (JobTriggerKind.Schedule or JobTriggerKind.Commit))
                throw JobServiceException.BadRequest("Unknown trigger kind.");
            var error = JobScheduleCalculator.Validate(trigger);
            if (error is not null)
                throw JobServiceException.BadRequest(error);
        }

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
            ["rev-parse", "--show-toplevel"], fullPath, TimeSpan.FromSeconds(5), cancellationToken);
        if (result.TimedOut || result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StdOut))
            throw JobServiceException.BadRequest("Automations must target a Git repository root.");

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(result.StdOut.Trim()));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!root.Equals(fullPath, comparison))
            throw JobServiceException.BadRequest($"Project path must be the Git repository root: {root}");
        return root;
    }

    private static JobResponse ToResponse(JobDefinitionRecord job) => new(
        job.Id, job.Name, job.ProjectPath, job.Llm, job.EnvironmentId, job.EnvironmentName,
        job.Prompt, job.TimeoutMinutes, job.Enabled, job.CreatedUtc, job.UpdatedUtc, job.DeletedUtc,
        job.Triggers.ToList());

    private static JobRunResponse ToResponse(JobRunRecord run) => new(
        run.Id, run.JobId, run.JobName, run.TriggerKind, run.Status, run.ProjectPath, run.Llm,
        run.EnvironmentName, run.SessionId, run.TimeoutMinutes, run.QueuedUtc, run.StartedUtc,
        run.EndedUtc, run.ExitCode, run.ErrorMessage, run.CancelRequested);
}

public sealed class JobServiceException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;

    public static JobServiceException BadRequest(string message) => new(400, message);
    public static JobServiceException NotFound(string message) => new(404, message);
    public static JobServiceException Conflict(string message) => new(409, message);
}
