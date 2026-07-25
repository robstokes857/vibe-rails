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
    List<JobTriggerDto> Triggers);

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
    List<JobTriggerRequest> Triggers);

public sealed record UpdateJobRequest(
    string Name,
    string ProjectPath,
    LLM Llm,
    int? EnvironmentId,
    string Prompt,
    int? TimeoutMinutes,
    bool Enabled,
    List<JobTriggerRequest> Triggers);

// A Job run is a recorded native terminal session. SessionId links to the Sessions row (and its
// SessionLogs / TerminalSessionLogs) so the Jobs UI can replay it with the same xterm player the
// Chat History sidebar uses. The session is tagged with this run's id and hidden from Chat History.
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

public sealed record JobRunListResponse(List<JobRunResponse> Runs);

public sealed record JobActionResponse(bool Success, string Message, string? RunId = null);

/// <summary>
/// State of the OS scheduled task that runs `vb --job-tick` every minute. Without it, Jobs only
/// fire while the dashboard is open; with it they fire whenever the user is logged in.
/// </summary>
public sealed record JobSchedulerStatusResponse(bool Installed, bool Supported, string Platform);

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
    IReadOnlyList<JobTriggerDto> Triggers);

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
    int? OwnerProcessId);
