using VibeRails.Services;

namespace VibeRails.DTOs;

public enum JobExecutionMode
{
    Review = 0,
    IsolatedWrite = 1,
    LiveWrite = 2
}

public enum JobTriggerKind
{
    Schedule = 0,
    Vca = 1,
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
    JobExecutionMode ExecutionMode,
    int TimeoutMinutes,
    bool Enabled,
    string? ExecutablePath,
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
    JobExecutionMode ExecutionMode,
    int TimeoutMinutes,
    bool Enabled,
    List<JobTriggerRequest> Triggers);

public sealed record UpdateJobRequest(
    string Name,
    string ProjectPath,
    LLM Llm,
    int? EnvironmentId,
    string Prompt,
    JobExecutionMode ExecutionMode,
    int TimeoutMinutes,
    bool Enabled,
    List<JobTriggerRequest> Triggers);

public sealed record JobRunResponse(
    string Id,
    long JobId,
    string JobName,
    JobTriggerKind TriggerKind,
    JobRunStatus Status,
    string ProjectPath,
    LLM Llm,
    string? EnvironmentName,
    JobExecutionMode ExecutionMode,
    DateTime QueuedUtc,
    DateTime? StartedUtc,
    DateTime? EndedUtc,
    int? ExitCode,
    string? ResultText,
    string? ErrorMessage,
    string? WorkspacePath,
    bool CancelRequested);

public sealed record JobRunListResponse(List<JobRunResponse> Runs);

public sealed record JobRunLogResponse(
    long Sequence,
    DateTime TimestampUtc,
    string Stream,
    string Content);

public sealed record JobRunLogsResponse(string RunId, List<JobRunLogResponse> Logs, long LastSequence);

public sealed record JobWorkerStatusResponse(
    string State,
    bool Installed,
    bool Running,
    bool NeedsRepair,
    string? Version,
    DateTime? HeartbeatUtc,
    string Message);

public sealed record JobActionResponse(bool Success, string Message, string? RunId = null);

public sealed record JobDefinitionRecord(
    long Id,
    string Name,
    string ProjectPath,
    LLM Llm,
    int? EnvironmentId,
    string? EnvironmentName,
    string? EnvironmentPath,
    string EnvironmentArgs,
    string Prompt,
    JobExecutionMode ExecutionMode,
    int TimeoutMinutes,
    bool Enabled,
    string? ExecutablePath,
    DateTime CreatedUtc,
    DateTime UpdatedUtc,
    DateTime? DeletedUtc,
    IReadOnlyList<JobTriggerDto> Triggers);

public sealed record JobRunRecord(
    string Id,
    long JobId,
    long? TriggerId,
    JobTriggerKind TriggerKind,
    string TriggerKey,
    JobRunStatus Status,
    string JobName,
    string ProjectPath,
    LLM Llm,
    int? EnvironmentId,
    string? EnvironmentName,
    string? EnvironmentPath,
    string EnvironmentArgs,
    string Prompt,
    JobExecutionMode ExecutionMode,
    int TimeoutMinutes,
    string? ExecutablePath,
    string? TriggerContextJson,
    DateTime QueuedUtc,
    DateTime? StartedUtc,
    DateTime? EndedUtc,
    int? ExitCode,
    string? ResultText,
    string? ErrorMessage,
    string? WorkspacePath,
    bool CancelRequested,
    string? OwnerInstanceId,
    int? OwnerProcessId);

public sealed record JobWorkerLeaseRecord(
    string InstanceId,
    int ProcessId,
    string Version,
    DateTime StartedUtc,
    DateTime HeartbeatUtc);
