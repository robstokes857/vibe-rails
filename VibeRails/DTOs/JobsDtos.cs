using VibeRails.Services;

namespace VibeRails.DTOs;

public enum JobTriggerKind
{
    Schedule = 0,
    // 1 was the removed pre-commit VCA trigger; the value is intentionally skipped so existing
    // Commit/Manual numeric values stay stable. Before-commit automations use PreCommit = 4.
    Commit = 2,
    Manual = 3,
    PreCommit = 4
}

public enum JobScheduleKind
{
    Interval = 0,
    Daily = 1,
    Weekly = 2
}

public enum JobRunStatus
{
    Queued = 0,
    Running = 1,
    Succeeded = 2,
    Failed = 3,
    Cancelled = 4,
    TimedOut = 5,
    Interrupted = 6
}

/// <summary>
/// One ordered action in an Automation. Existing Automations are represented as one Worker
/// action; Script actions may run before or after it, or form a script-only Automation.
/// </summary>
public enum JobActionKind
{
    Worker = 0,
    Script = 1
}

/// <summary>The explicitly selected interpreter for a repository-local script action.</summary>
public enum JobScriptRuntime
{
    Python = 0,
    PowerShell = 1,
    Bash = 2
}

/// <summary>Per-action status inside one Automation run.</summary>
public enum JobRunActionStatus
{
    Pending = 0,
    Running = 1,
    Succeeded = 2,
    Failed = 3,
    Skipped = 4,
    Cancelled = 5,
    TimedOut = 6
}

/// <summary>
/// The shared outcome contract for a Job-run process: the exit code reported for each terminal
/// <see cref="JobRunStatus"/> and the user-facing cancellation message. It lives beside the enum
/// so JobStore's atomic completion SQL and the JobRunner agree on one mapping without the DB
/// layer referencing Services.
/// </summary>
public static class JobRunOutcome
{
    public const string CancelledMessage = "Automation was cancelled.";

    /// <summary>
    /// Exit code for "this process had no run to execute" — the run id was missing from the DB, or
    /// another process claimed it first. Deliberately distinct from every status code: nothing went
    /// wrong with a run here, because no run was ever started by this process. Mapping it onto
    /// <see cref="JobRunStatus.Queued"/> would tell a supervisor "still queued" for the
    /// already-claimed case, which is the opposite of what happened.
    /// </summary>
    public const int CouldNotStartExitCode = 5;

    /// <summary>
    /// Exit codes are the contract for anything supervising this process, so they must be truthful
    /// rather than uniformly 0 — a caller should not have to read SQLite to learn what happened.
    /// Queued is unmapped on purpose: a process that reaches an exit code has, by definition, gone
    /// past queued, so seeing it here means a caller passed a status that cannot occur.
    /// </summary>
    public static int ToExitCode(JobRunStatus status) => status switch
    {
        JobRunStatus.Succeeded => 0,
        JobRunStatus.Failed => 1,
        JobRunStatus.TimedOut => 2,
        JobRunStatus.Cancelled => 3,
        JobRunStatus.Interrupted => 4,
        _ => CouldNotStartExitCode
    };
}

// TimeoutMinutes is opt-in throughout: null means the run lives until its CLI exits or the user
// closes its terminal window, which is the default. It is stored as 0 in SQLite (the column is NOT
// NULL) and mapped back to null on read, so no migration was needed to make it optional.
public sealed record JobTriggerDto(
    long Id,
    JobTriggerKind Kind,
    JobScheduleKind? ScheduleKind = null,
    int? IntervalMinutes = null,
    string? LocalTime = null,
    int DaysOfWeekMask = 0,
    string? TimeZoneId = null,
    DateTime? NextRunUtc = null,
    DateTime? LastRunUtc = null);

public sealed record JobResponse(
    long Id,
    string Name,
    string ProjectPath,
    LLM Llm,
    int? EnvironmentId,
    string? EnvironmentName,
    string Prompt,
    int? TimeoutMinutes,
    bool Enabled,
    DateTime CreatedUtc,
    DateTime UpdatedUtc,
    DateTime? DeletedUtc,
    List<JobTriggerDto> Triggers,
    bool LaunchMinimized = false,
    List<JobActionDto>? Actions = null);

public sealed record JobListResponse(List<JobResponse> Jobs);

public sealed record JobTriggerRequest(
    JobTriggerKind Kind,
    JobScheduleKind? ScheduleKind = null,
    int? IntervalMinutes = null,
    string? LocalTime = null,
    int DaysOfWeekMask = 0,
    string? TimeZoneId = null);

public sealed record CreateJobRequest(
    string Name,
    string ProjectPath,
    LLM Llm,
    int? EnvironmentId,
    string Prompt,
    int? TimeoutMinutes,
    bool Enabled,
    List<JobTriggerRequest> Triggers,
    bool LaunchMinimized = false,
    List<JobActionRequest>? Actions = null);

public sealed record UpdateJobRequest(
    string Name,
    string ProjectPath,
    LLM Llm,
    int? EnvironmentId,
    string Prompt,
    int? TimeoutMinutes,
    bool Enabled,
    List<JobTriggerRequest> Triggers,
    bool LaunchMinimized = false,
    List<JobActionRequest>? Actions = null);

/// <summary>
/// Client-supplied action shape. Script paths and working directories may arrive absolute from
/// the local file picker, but the service normalizes them to repository-relative paths before
/// persistence. ApprovedHash is output-only in normal UI use: the server computes it from the
/// current regular file whenever the Automation is saved.
/// </summary>
public sealed record JobActionRequest(
    string? Id,
    JobActionKind Kind,
    int? EnvironmentId = null,
    string? ScriptPath = null,
    JobScriptRuntime? ScriptRuntime = null,
    List<string>? Arguments = null,
    string? WorkingDirectory = null,
    int? TimeoutSeconds = null,
    string? ApprovedHash = null);

public sealed record JobActionDto(
    string Id,
    int Position,
    JobActionKind Kind,
    int? EnvironmentId,
    string? EnvironmentName,
    LLM Llm,
    string? ScriptPath,
    JobScriptRuntime? ScriptRuntime,
    List<string> Arguments,
    string? WorkingDirectory,
    int? TimeoutSeconds,
    string? ApprovedHash);

// A Job run is a recorded native terminal session. SessionId links to the Sessions row (and its
// SessionLogs / TerminalSessionLogs) so the Jobs UI can replay it with the same xterm player the
// Chat History sidebar uses. The session is tagged with this run's id and hidden from Chat History
// while the run is visible in Automation history. Soft-removing the run clears that session tag so
// the retained recording remains reachable from Chat History.
public sealed record JobRunResponse(
    string Id,
    long JobId,
    string JobName,
    JobTriggerKind TriggerKind,
    JobRunStatus Status,
    string ProjectPath,
    LLM Llm,
    string? EnvironmentName,
    string? SessionId,
    int? TimeoutMinutes,
    DateTime QueuedUtc,
    DateTime? StartedUtc,
    DateTime? EndedUtc,
    int? ExitCode,
    string? ErrorMessage,
    bool CancelRequested,
    List<JobRunActionDto>? Actions = null);

public sealed record JobRunActionDto(
    string Id,
    int Position,
    JobActionKind Kind,
    JobRunActionStatus Status,
    int? EnvironmentId,
    string? EnvironmentName,
    LLM Llm,
    string? ScriptPath,
    JobScriptRuntime? ScriptRuntime,
    List<string> Arguments,
    string? WorkingDirectory,
    int? TimeoutSeconds,
    string? ApprovedHash,
    string? SessionId,
    DateTime? StartedUtc,
    DateTime? EndedUtc,
    int? ExitCode,
    string? ErrorMessage,
    string StandardOutput,
    string StandardError);

/// <summary>
/// Pagination metadata is nullable because not every path that returns runs is paged. The capped
/// recent-runs feed (<c>GET /api/v1/jobs/runs</c> without a job id) applies a LIMIT and has no
/// total, so it omits all three rather than reporting the truncated page size as the total.
/// </summary>
public sealed record JobRunListResponse(
    List<JobRunResponse> Runs,
    int? TotalRuns = null,
    int? Page = null,
    int? PageSize = null);

// One row per automation rather than one per run. A job on a 15-minute schedule otherwise fills
// any capped run list and hides every other automation's history behind it, so the summary is
// computed in SQL over the current project's visible history instead of by grouping a truncated
// page client-side.
public sealed record JobRunSummaryResponse(
    long JobId,
    string JobName,
    int TotalRuns,
    int ActiveRuns,
    string LastRunId,
    JobRunStatus LastStatus,
    JobTriggerKind LastTriggerKind,
    LLM LastLlm,
    string? LastEnvironmentName,
    DateTime LastQueuedUtc,
    DateTime? LastStartedUtc,
    DateTime? LastEndedUtc,
    int? LastExitCode,
    string? LastErrorMessage);

public sealed record JobRunSummaryListResponse(List<JobRunSummaryResponse> Summaries);

// Deletion takes explicit ids rather than a job id plus a filter, so the UI can remove exactly
// the runs the user selected — including a hand-picked subset of one job's history.
public sealed record DeleteJobRunsRequest(List<string>? RunIds);

// Deleted is the number of rows hidden with DeletedUTC. The name stays stable for API consumers;
// run/session/log rows are retained rather than physically deleted.
public sealed record DeleteJobRunsResponse(bool Success, string Message, int Deleted, int Skipped);

// TotalRuns/ActiveRuns are aggregates over the job's whole history; LastRun is the most recent
// row, so the summary table can show real status without a second query per job.
public sealed record JobRunSummaryRecord(int TotalRuns, int ActiveRuns, JobRunRecord LastRun);

public sealed record JobRunPageRecord(
    int TotalRuns,
    int Page,
    int PageSize,
    IReadOnlyList<JobRunRecord> Runs);

public sealed record JobActionResponse(bool Success, string Message, string? RunId = null);

public sealed record JobDefinitionRecord(
    long Id,
    string Name,
    string ProjectPath,
    LLM Llm,
    int? EnvironmentId,
    string? EnvironmentName,
    string Prompt,
    int? TimeoutMinutes,
    bool Enabled,
    DateTime CreatedUtc,
    DateTime UpdatedUtc,
    DateTime? DeletedUtc,
    IReadOnlyList<JobTriggerDto> Triggers,
    bool LaunchMinimized = false,
    IReadOnlyList<JobActionRecord>? Actions = null);

public sealed record JobActionRecord(
    string Id,
    long JobId,
    int Position,
    JobActionKind Kind,
    int? EnvironmentId,
    string? EnvironmentName,
    LLM Llm,
    string? ScriptPath,
    JobScriptRuntime? ScriptRuntime,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory,
    int? TimeoutSeconds,
    string? ApprovedHash);

public sealed record JobRunRecord(
    string Id,
    long JobId,
    JobTriggerKind TriggerKind,
    string TriggerKey,
    JobRunStatus Status,
    string JobName,
    string ProjectPath,
    LLM Llm,
    int? EnvironmentId,
    string? EnvironmentName,
    int? TimeoutMinutes,
    string? SessionId,
    DateTime QueuedUtc,
    DateTime? StartedUtc,
    DateTime? EndedUtc,
    int? ExitCode,
    string? ErrorMessage,
    bool CancelRequested,
    int? OwnerProcessId,
    bool LaunchMinimized = false,
    IReadOnlyList<JobRunActionRecord>? Actions = null);

public sealed record JobRunActionRecord(
    string Id,
    string RunId,
    string? SourceActionId,
    int Position,
    JobActionKind Kind,
    JobRunActionStatus Status,
    int? EnvironmentId,
    string? EnvironmentName,
    LLM Llm,
    string? ScriptPath,
    JobScriptRuntime? ScriptRuntime,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory,
    int? TimeoutSeconds,
    string? ApprovedHash,
    string? SessionId,
    DateTime? StartedUtc,
    DateTime? EndedUtc,
    int? ExitCode,
    string? ErrorMessage,
    string StandardOutput,
    string StandardError);
