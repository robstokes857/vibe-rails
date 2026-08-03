using VibeRails.Services;

namespace VibeRails.DTOs;

public enum JobTriggerKind
{
    Schedule = 0,
    // 1 was the removed pre-commit VCA trigger; the value is intentionally skipped so existing
    // Commit/Manual numeric values stay stable.
    Commit = 2,
    Manual = 3
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
    bool LaunchMinimized = false);

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
    bool LaunchMinimized = false);

public sealed record UpdateJobRequest(
    string Name,
    string ProjectPath,
    LLM Llm,
    int? EnvironmentId,
    string Prompt,
    int? TimeoutMinutes,
    bool Enabled,
    List<JobTriggerRequest> Triggers,
    bool LaunchMinimized = false);

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
    bool CancelRequested);

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
    bool LaunchMinimized = false);

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
    bool LaunchMinimized = false);
