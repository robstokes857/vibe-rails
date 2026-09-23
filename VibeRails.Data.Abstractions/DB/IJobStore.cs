using VibeRails.DTOs;
using VibeRails.Services;
using TokenSaver;
using TokenSaver.Pipeline;

namespace VibeRails.DB;


public interface IJobStore
{
    Task<IReadOnlyList<JobDefinitionRecord>> GetJobsAsync(string? projectPath = null, bool includeDeleted = false, CancellationToken cancellationToken = default);
    Task<JobDefinitionRecord?> GetJobAsync(long id, CancellationToken cancellationToken = default);
    Task<JobDefinitionRecord> CreateJobAsync(CreateJobRequest request, CancellationToken cancellationToken = default);
    Task<JobDefinitionRecord?> UpdateJobAsync(long id, UpdateJobRequest request, CancellationToken cancellationToken = default);
    Task<bool> SoftDeleteJobAsync(long id, CancellationToken cancellationToken = default);
    Task<int> CountEnabledJobsAsync(CancellationToken cancellationToken = default);
    Task<int> CountJobsForEnvironmentAsync(int environmentId, CancellationToken cancellationToken = default);
    Task<bool> TryDeleteEnvironmentIfUnusedAsync(int environmentId, Action stageFilesystemDeletion, CancellationToken cancellationToken = default);
    Task<bool> TryAcquireOrRenewSchedulerLeaseAsync(
        string ownerId,
        DateTime nowUtc,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);
    Task<bool> ReleaseSchedulerLeaseAsync(string ownerId, CancellationToken cancellationToken = default);
    Task<string?> EnqueueManualRunAsync(long jobId, CancellationToken cancellationToken = default);
    Task<string?> EnqueueRetryAsync(string runId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> EnqueueEventRunsAsync(string projectPath, JobTriggerKind kind, string eventKey, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> EnqueueDueSchedulesAsync(DateTime nowUtc, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<JobRunRecord>> GetRunsAsync(long? jobId = null, int limit = 100, CancellationToken cancellationToken = default);
    Task<JobRunPageRecord> GetRunsPageAsync(long jobId, int page = 1, int pageSize = 50, CancellationToken cancellationToken = default);
    Task<JobRunRecord?> GetRunAsync(string runId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<JobRunSummaryRecord>> GetRunSummariesAsync(string projectPath, CancellationToken cancellationToken = default);
    Task<(int Deleted, int Skipped)> SoftDeleteRunsAsync(IReadOnlyList<string> runIds, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<JobRunRecord>> GetQueuedRunsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<JobRunRecord>> GetActiveRunsAsync(CancellationToken cancellationToken = default);
    Task<int> CountRunningRunsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<JobRunRecord>> GetLaunchableRunsAsync(CancellationToken cancellationToken = default);
    Task<bool> TryMarkLaunchedAsync(string runId, CancellationToken cancellationToken = default);
    Task<int> FailStalledLaunchesAsync(TimeSpan grace, CancellationToken cancellationToken = default);
    Task<bool> StartRunAsync(string runId, int processId, CancellationToken cancellationToken = default);
    Task CompleteRunAsync(string runId, JobRunStatus status, int? exitCode, string? errorMessage, CancellationToken cancellationToken = default);
    Task<JobRunStatus> CompleteIdleRunAsync(string runId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<JobRunActionRecord>> GetRunActionsAsync(string runId, CancellationToken cancellationToken = default);
    Task<bool> StartRunActionAsync(string runId, string actionId, CancellationToken cancellationToken = default);
    Task LinkRunTerminalSessionAsync(string runId, string sessionId, CancellationToken cancellationToken = default);
    Task LinkRunActionSessionAsync(string runId, string actionId, string sessionId, CancellationToken cancellationToken = default);
    Task CompleteRunActionAsync(
        string runId,
        string actionId,
        JobRunActionStatus status,
        int? exitCode,
        string? errorMessage,
        string? standardOutput,
        string? standardError,
        CancellationToken cancellationToken = default);
    Task<bool> RequestCancelAsync(string runId, CancellationToken cancellationToken = default);
    Task<bool> IsCancelRequestedAsync(string runId, CancellationToken cancellationToken = default);
}

