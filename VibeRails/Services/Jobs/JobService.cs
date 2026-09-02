using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Utils;

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
    Task<JobRunListResponse> GetRunsPageAsync(long jobId, int page, int pageSize, CancellationToken cancellationToken = default);
    Task<JobRunResponse> GetRunAsync(string runId, CancellationToken cancellationToken = default);
    Task<JobRunSummaryListResponse> GetRunSummariesAsync(string? projectPath, CancellationToken cancellationToken = default);
    Task<DeleteJobRunsResponse> DeleteRunsAsync(IReadOnlyList<string> runIds, CancellationToken cancellationToken = default);
    Task<JobActionResponse> CancelRunAsync(string runId, CancellationToken cancellationToken = default);
    Task<JobActionResponse> RetryRunAsync(string runId, CancellationToken cancellationToken = default);
}

public sealed class JobService(
    IJobStore store,
    IRepository repository,
    IJobExecutableResolver executableResolver,
    IJobScheduler scheduler,
    IJobDaemonKicker? daemonKicker = null,
    IAutomationScriptService? automationScriptService = null) : IJobService
{
    private const int MaximumNameLength = 100;
    private const int MaximumPromptLength = 50_000;
    private const int MinimumTimeoutMinutes = 1;
    private const int MaximumRunDeleteBatchSize = 100;
    private const int MaximumRunIdLength = 128;
    private const int MaximumActions = 20;

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
        var normalized = await ValidateAndNormalizeAsync(request, cancellationToken);
        var created = await store.CreateJobAsync(normalized, cancellationToken);
        return ToResponse(created);
    }

    public async Task<JobResponse> UpdateJobAsync(long id, UpdateJobRequest request, CancellationToken cancellationToken = default)
    {
        var existing = await store.GetJobAsync(id, cancellationToken);
        if (existing is null || existing.DeletedUtc is not null)
            throw JobServiceException.NotFound("Automation not found.");
        // An older client knows only the top-level EnvironmentId fields. Preserve an existing
        // workflow when that client edits an unrelated setting instead of collapsing it to one
        // Worker action.
        var approveScriptChanges = request.Actions is not null;
        if (request.Actions is null && existing.Actions is { Count: > 0 })
            request = request with { Actions = existing.Actions.Select(ToRequest).ToList() };

        var normalized = await ValidateAndNormalizeAsync(request, approveScriptChanges, cancellationToken);
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
        ValidateRunRequirements(job.Actions, job.Llm);

        // Once validation has completed, do not let RequestAborted abandon a half-enqueued run.
        cancellationToken.ThrowIfCancellationRequested();
        var runId = await store.EnqueueManualRunAsync(id, CancellationToken.None)
            ?? throw JobServiceException.Conflict("The Automation could not be queued.");

        // Every Automation run - manual, retried or scheduled - is launched the same way: the
        // scheduler claims the queued row and JobLaunchService opens a real OS terminal window for
        // it. Kick only wakes that loop early; it deliberately does NOT reserve the run here, so
        // there is exactly one launcher and one kind of window an Automation can run in.
        scheduler.Kick();
        await JobDaemonWakeup.TryKickAsync(daemonKicker, CancellationToken.None);
        return new JobActionResponse(true, "Automation queued.", runId);
    }

    public async Task<JobRunListResponse> GetRunsAsync(long? jobId, int limit, CancellationToken cancellationToken = default)
    {
        // No pagination metadata: GetRunsAsync applies a LIMIT, so runs.Count is the size of the
        // truncated slice and reporting it as TotalRuns would tell a caller asking for 100 that
        // exactly 100 runs exist. Callers that need a real total use GetRunsPageAsync.
        var runs = await store.GetRunsAsync(jobId, limit, cancellationToken);
        return new JobRunListResponse(runs.Select(ToResponse).ToList());
    }

    public async Task<JobRunListResponse> GetRunsPageAsync(
        long jobId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var result = await store.GetRunsPageAsync(jobId, page, pageSize, cancellationToken);
        return new JobRunListResponse(
            result.Runs.Select(ToResponse).ToList(),
            result.TotalRuns,
            result.Page,
            result.PageSize);
    }

    public async Task<JobRunResponse> GetRunAsync(string runId, CancellationToken cancellationToken = default)
    {
        var run = await store.GetRunAsync(runId, cancellationToken)
            ?? throw JobServiceException.NotFound("Automation run not found.");
        return ToResponse(run);
    }

    public async Task<JobRunSummaryListResponse> GetRunSummariesAsync(
        string? projectPath,
        CancellationToken cancellationToken = default)
    {
        // Never fall back to a global aggregation: this endpoint is polled every five seconds.
        if (string.IsNullOrWhiteSpace(projectPath))
            return new JobRunSummaryListResponse([]);

        RequireValidProjectPath(projectPath);

        var summaries = await store.GetRunSummariesAsync(projectPath, cancellationToken);
        return new JobRunSummaryListResponse(summaries.Select(summary =>
        {
            var last = summary.LastRun;
            return new JobRunSummaryResponse(
                last.JobId,
                last.JobName,
                summary.TotalRuns,
                summary.ActiveRuns,
                last.Id,
                last.Status,
                last.TriggerKind,
                last.Llm,
                last.EnvironmentName,
                last.QueuedUtc,
                last.StartedUtc,
                last.EndedUtc,
                last.ExitCode,
                last.ErrorMessage);
        }).ToList());
    }

    /// <summary>
    /// Removing nothing is a client bug rather than a server error, so an empty list is rejected
    /// outright instead of silently reporting a successful no-op.
    /// </summary>
    public async Task<DeleteJobRunsResponse> DeleteRunsAsync(
        IReadOnlyList<string> runIds,
        CancellationToken cancellationToken = default)
    {
        if (runIds.Count > MaximumRunDeleteBatchSize)
            throw JobServiceException.BadRequest($"Remove at most {MaximumRunDeleteBatchSize} runs at a time.");
        if (runIds.Any(id => id is not null && id.Length > MaximumRunIdLength))
            throw JobServiceException.BadRequest("A run id is too long.");

        var ids = runIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (ids.Count == 0)
            throw JobServiceException.BadRequest("Select at least one run to remove.");

        var (deleted, skipped) = await store.SoftDeleteRunsAsync(ids, cancellationToken);

        // Removing nothing is never a success. Either the runs were live (a conflict the user can
        // act on by stopping them) or they matched no visible row at all — reporting either as
        // "Removed 0 runs" would leave the user staring at rows they were told had gone.
        if (deleted == 0)
        {
            throw skipped > 0
                ? JobServiceException.Conflict("A run that is queued or still running cannot be removed. Stop it first.")
                : JobServiceException.NotFound("Those runs are no longer in Automation history.");
        }

        var message = skipped > 0
            ? $"Removed {deleted} {Pluralize(deleted, "run")} from Automation history. {skipped} still running and {(skipped == 1 ? "was" : "were")} kept."
            : $"Removed {deleted} {Pluralize(deleted, "run")} from Automation history.";

        return new DeleteJobRunsResponse(true, message, deleted, skipped);
    }

    private static string Pluralize(int count, string noun) => count == 1 ? noun : noun + "s";

    /// <summary>
    /// The store normalizes project paths through <see cref="Path.GetFullPath(string)"/>, which
    /// throws on syntactically invalid input. A caller sending a malformed path made a bad request,
    /// so reject it as one instead of letting it surface as an unhandled 500.
    /// </summary>
    private static void RequireValidProjectPath(string projectPath)
    {
        try
        {
            _ = Path.GetFullPath(projectPath.Trim());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw JobServiceException.BadRequest("That project path is not a valid path.");
        }
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
        ValidateRunRequirements(source.Actions, source.Llm);

        var retryId = await store.EnqueueRetryAsync(runId, cancellationToken)
            ?? throw JobServiceException.Conflict("Only completed runs for active Automations can be retried.");
        scheduler.Kick();
        await JobDaemonWakeup.TryKickAsync(daemonKicker, CancellationToken.None);
        return new JobActionResponse(true, "Automation retry queued.", retryId);
    }

    private async Task<CreateJobRequest> ValidateAndNormalizeAsync(
        CreateJobRequest request,
        CancellationToken cancellationToken)
    {
        var projectPath = await ValidateProjectAsync(request.ProjectPath, cancellationToken);
        var (llm, environmentId, prompt, actions) = await ValidateCommonAsync(
            request.Name,
            request.EnvironmentId,
            request.Actions,
            request.TimeoutMinutes,
            request.Triggers,
            projectPath,
            approveScriptChanges: true,
            enabled: request.Enabled,
            cancellationToken);
        return request with
        {
            Name = request.Name.Trim(),
            ProjectPath = projectPath,
            Llm = llm,
            EnvironmentId = environmentId,
            Prompt = prompt,
            Triggers = request.Triggers?.ToList() ?? [],
            Actions = actions
        };
    }

    private async Task<UpdateJobRequest> ValidateAndNormalizeAsync(
        UpdateJobRequest request,
        bool approveScriptChanges,
        CancellationToken cancellationToken)
    {
        var projectPath = await ValidateProjectAsync(request.ProjectPath, cancellationToken);
        var (llm, environmentId, prompt, actions) = await ValidateCommonAsync(
            request.Name,
            request.EnvironmentId,
            request.Actions,
            request.TimeoutMinutes,
            request.Triggers,
            projectPath,
            approveScriptChanges,
            request.Enabled,
            cancellationToken);
        return request with
        {
            Name = request.Name.Trim(),
            ProjectPath = projectPath,
            Llm = llm,
            EnvironmentId = environmentId,
            Prompt = prompt,
            Triggers = request.Triggers?.ToList() ?? [],
            Actions = actions
        };
    }

    private async Task<(LLM Llm, int? EnvironmentId, string Prompt, List<JobActionRequest> Actions)> ValidateCommonAsync(
        string name,
        int? legacyEnvironmentId,
        IReadOnlyList<JobActionRequest>? requestedActions,
        int? timeoutMinutes,
        IReadOnlyList<JobTriggerRequest>? triggers,
        string projectPath,
        bool approveScriptChanges,
        bool enabled,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > MaximumNameLength)
            throw JobServiceException.BadRequest($"Automation name is required and must be at most {MaximumNameLength} characters.");

        var incoming = requestedActions is { Count: > 0 }
            ? requestedActions.ToList()
            : legacyEnvironmentId is int environmentId
                ? [new JobActionRequest(null, JobActionKind.Worker, environmentId)]
                : [];
        if (incoming.Count == 0)
            throw JobServiceException.BadRequest("Add at least one Worker or repository script to the Automation.");
        if (incoming.Count > MaximumActions)
            throw JobServiceException.BadRequest($"An Automation can have at most {MaximumActions} actions.");
        if (incoming.Count(action => action.Kind == JobActionKind.Worker) > 1)
            throw JobServiceException.BadRequest("An Automation can contain at most one Worker.");

        var environments = await repository.GetAllEnvironmentsAsync(cancellationToken);
        var normalizedActions = new List<JobActionRequest>(incoming.Count);
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        LLM resolvedLlm = LLM.NotSet;
        int? resolvedEnvironmentId = null;
        var resolvedPrompt = string.Empty;
        foreach (var action in incoming)
        {
            if (!Enum.IsDefined(typeof(JobActionKind), action.Kind))
                throw JobServiceException.BadRequest("Unknown Automation action kind.");

            var id = Guid.TryParse(action.Id, out var parsedId)
                ? parsedId.ToString()
                : Guid.NewGuid().ToString();
            if (!seenIds.Add(id))
                id = Guid.NewGuid().ToString();

            if (action.Kind == JobActionKind.Worker)
            {
                if (action.EnvironmentId is not int actionEnvironmentId)
                    throw JobServiceException.BadRequest("Choose an Environment for every Worker action.");

                // Scoped to the Automation's project. Environment ids are global, so accepting an
                // id from another project would launch its arguments and permissions here.
                var environment = environments.FirstOrDefault(item =>
                    item.Id == actionEnvironmentId
                    && ProjectPathComparer.IsVisibleIn(item.ProjectPath, projectPath))
                    ?? throw JobServiceException.BadRequest("The selected Environment no longer exists.");

                if (environment.LLM is not (LLM.Codex or LLM.Claude or LLM.Antigravity
                    or LLM.Copilot or LLM.OpenCode or LLM.Glm52 or LLM.Grok46 or LLM.Glm53))
                {
                    throw JobServiceException.BadRequest("The selected LLM cannot run as an Automation.");
                }

                var prompt = environment.CustomPrompt?.Trim() ?? string.Empty;
                if (prompt.Length == 0)
                    throw JobServiceException.BadRequest("The selected Environment needs an Initial Message before it can run as an Automation.");
                if (prompt.Length > MaximumPromptLength)
                    throw JobServiceException.BadRequest($"Initial message must be at most {MaximumPromptLength} characters.");

                resolvedLlm = environment.LLM;
                resolvedEnvironmentId = environment.Id;
                resolvedPrompt = prompt;
                normalizedActions.Add(new JobActionRequest(id, JobActionKind.Worker, environment.Id));
                continue;
            }

            if (automationScriptService is null)
                throw JobServiceException.BadRequest("Repository script Automations are not available in this process.");

            try
            {
                normalizedActions.Add(await automationScriptService.NormalizeAsync(
                    projectPath,
                    action with { Id = id },
                    cancellationToken,
                    approveScriptChanges));
            }
            catch (AutomationScriptValidationException) when (!approveScriptChanges && !enabled)
            {
                // Disabling is the safety valve and must never be blocked by a script that has
                // changed, gone missing, or lost its interpreter — that is exactly when the user
                // needs to stop the schedule. The saved snapshot is carried verbatim (same hash),
                // so nothing is re-approved and the run-time check still fails closed.
                normalizedActions.Add(action with
                {
                    Id = id,
                    EnvironmentId = null,
                    Arguments = action.Arguments?.ToList() ?? []
                });
            }
            catch (AutomationScriptValidationException ex)
            {
                throw JobServiceException.BadRequest(ex.Message);
            }
        }

        // A timeout is optional: with none set the run lives until its CLI exits or the user closes
        // its window. Script actions may also impose their own shorter bound.
        if (timeoutMinutes is not null && timeoutMinutes is < MinimumTimeoutMinutes or > MaximumTimeoutMinutes)
            throw JobServiceException.BadRequest($"Timeout must be {MinimumTimeoutMinutes}–{MaximumTimeoutMinutes} minutes, or left off entirely.");

        ValidateTriggers(triggers);
        return (resolvedLlm, resolvedEnvironmentId, resolvedPrompt, normalizedActions);
    }

    private static void ValidateTriggers(IReadOnlyList<JobTriggerRequest>? triggers)
    {
        var triggerList = triggers ?? [];
        var duplicate = triggerList.GroupBy(trigger => trigger.Kind).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw JobServiceException.BadRequest($"Only one {duplicate.Key} trigger may be configured per Automation.");
        if (triggerList.Any(trigger => trigger.Kind == JobTriggerKind.PreCommit)
            && triggerList.Any(trigger => trigger.Kind == JobTriggerKind.Commit))
        {
            throw JobServiceException.BadRequest(
                "Choose either Before each commit or After each commit; one Automation cannot use both.");
        }
        foreach (var trigger in triggerList)
        {
            if (trigger.Kind == JobTriggerKind.Manual)
                throw JobServiceException.BadRequest("Run Now is always available and is not stored as a trigger.");
            if (trigger.Kind is not (JobTriggerKind.Schedule or JobTriggerKind.Commit or JobTriggerKind.PreCommit))
                throw JobServiceException.BadRequest("Unknown trigger kind.");
            var error = JobScheduleCalculator.Validate(trigger);
            if (error is not null)
                throw JobServiceException.BadRequest(error);
        }

    }

    private void ValidateRunRequirements(IReadOnlyList<JobRunActionRecord>? actions, LLM legacyLlm)
    {
        if (actions is not { Count: > 0 })
        {
            if (legacyLlm == LLM.NotSet || executableResolver.Resolve(legacyLlm) is null)
                throw JobServiceException.BadRequest($"The {legacyLlm} CLI is not available on PATH.");
            return;
        }

        foreach (var action in actions)
        {
            if (action.Kind == JobActionKind.Worker)
            {
                if (action.Llm == LLM.NotSet || executableResolver.Resolve(action.Llm) is null)
                    throw JobServiceException.BadRequest($"The {action.Llm} CLI is not available on PATH.");
                continue;
            }

            if (action.ScriptRuntime is not JobScriptRuntime runtime)
                throw JobServiceException.BadRequest("A script action is missing its runtime.");

            if (automationScriptService is null)
                throw JobServiceException.BadRequest("Repository script Automations are not available in this process.");

            var unavailable = automationScriptService.GetRuntimeUnavailableMessage(runtime);
            if (unavailable is not null)
                throw JobServiceException.BadRequest(unavailable);
        }
    }

    private void ValidateRunRequirements(IReadOnlyList<JobActionRecord>? actions, LLM legacyLlm) =>
        ValidateRunRequirements(
            actions?.Select(action => new JobRunActionRecord(
                action.Id,
                string.Empty,
                action.Id,
                action.Position,
                action.Kind,
                JobRunActionStatus.Pending,
                action.EnvironmentId,
                action.EnvironmentName,
                action.Llm,
                action.ScriptPath,
                action.ScriptRuntime,
                action.Arguments,
                action.WorkingDirectory,
                action.TimeoutSeconds,
                action.ApprovedHash,
                null,
                null,
                null,
                null,
                null,
                string.Empty,
                string.Empty)).ToList(),
            legacyLlm);

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
        job.Triggers.ToList(), job.LaunchMinimized,
        job.Actions?.Select(ToActionDto).ToList());

    private static JobRunResponse ToResponse(JobRunRecord run) => new(
        run.Id, run.JobId, run.JobName, run.TriggerKind, run.Status, run.ProjectPath, run.Llm,
        run.EnvironmentName, run.SessionId, run.TimeoutMinutes, run.QueuedUtc, run.StartedUtc,
        run.EndedUtc, run.ExitCode, run.ErrorMessage, run.CancelRequested,
        run.Actions?.Select(ToRunActionDto).ToList());

    private static JobActionRequest ToRequest(JobActionRecord action) => new(
        action.Id,
        action.Kind,
        action.EnvironmentId,
        action.ScriptPath,
        action.ScriptRuntime,
        action.Arguments.ToList(),
        action.WorkingDirectory,
        action.TimeoutSeconds,
        action.ApprovedHash);

    private static JobActionDto ToActionDto(JobActionRecord action) => new(
        action.Id,
        action.Position,
        action.Kind,
        action.EnvironmentId,
        action.EnvironmentName,
        action.Llm,
        action.ScriptPath,
        action.ScriptRuntime,
        action.Arguments.ToList(),
        action.WorkingDirectory,
        action.TimeoutSeconds,
        action.ApprovedHash);

    private static JobRunActionDto ToRunActionDto(JobRunActionRecord action) => new(
        action.Id,
        action.Position,
        action.Kind,
        action.Status,
        action.EnvironmentId,
        action.EnvironmentName,
        action.Llm,
        action.ScriptPath,
        action.ScriptRuntime,
        action.Arguments.ToList(),
        action.WorkingDirectory,
        action.TimeoutSeconds,
        action.ApprovedHash,
        action.SessionId,
        action.StartedUtc,
        action.EndedUtc,
        action.ExitCode,
        action.ErrorMessage,
        action.StandardOutput,
        action.StandardError);
}

public sealed class JobServiceException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;

    public static JobServiceException BadRequest(string message) => new(400, message);
    public static JobServiceException NotFound(string message) => new(404, message);
    public static JobServiceException Conflict(string message) => new(409, message);
}
