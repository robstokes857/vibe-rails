using VibeRails.DTOs;
using VibeRails.Services;
using TokenSaver;
using TokenSaver.Pipeline;

namespace VibeRails.DB;


public interface IJobStore
{
    /// <summary>Atomically creates a new-board review Worker, Job and stable recipe receipt, or returns that receipt.</summary>
    Task<long> EnsureBoardReviewRecipeAsync(string projectPath, string columnId, string recipeId, CancellationToken cancellationToken = default);
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
    Task<string?> EnqueueBoardCardRunAsync(string projectPath, long jobId, string cardKey, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<JobRunRecord>> GetBoardCardRunsAsync(string projectPath, string cardKey, CancellationToken cancellationToken = default,
        IReadOnlyList<string>? linkedRecordingIds = null);
    Task<string?> EnqueueRetryAsync(string runId, CancellationToken cancellationToken = default);
    /// <summary>Rerun a failed exact lane entry, preserving the source snapshot when one exists.</summary>
    Task<string?> EnqueueLaneRerunAsync(VibeRails.Services.Board.BoardLaneAutomationEvent entry,
        string? sourceRunId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> EnqueueEventRunsAsync(string projectPath, JobTriggerKind kind, string eventKey, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> EnqueueDueSchedulesAsync(DateTime nowUtc, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<JobRunRecord>> GetRunsAsync(long? jobId = null, int limit = 100, CancellationToken cancellationToken = default);
    Task<JobRunPageRecord> GetRunsPageAsync(long jobId, int page = 1, int pageSize = 50, CancellationToken cancellationToken = default);
    Task<JobRunRecord?> GetRunAsync(string runId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<JobRunSummaryRecord>> GetRunSummariesAsync(string projectPath, CancellationToken cancellationToken = default);
    Task<(int Deleted, int Skipped)> SoftDeleteRunsAsync(IReadOnlyList<string> runIds, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<JobRunRecord>> GetQueuedRunsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<JobRunRecord>> GetActiveRunsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<JobRunRecord>> GetLaunchableRunsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Counts the terminals open across the machine and claims the right to spawn this run's
    /// terminal in one write transaction, so roots launching at the same moment cannot each see
    /// room under <paramref name="maxOpenTerminals"/>. Open means Running, or Queued with a
    /// claimed <c>LaunchedUTC</c> whose process has not started yet.
    /// </summary>
    Task<JobLaunchClaim> TryClaimLaunchAsync(string runId, int maxOpenTerminals, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records that the root <paramref name="ownerId"/> has <paramref name="projectPath"/> open,
    /// until <paramref name="nowUtc"/> plus <paramref name="timeToLive"/>. Called every scheduler
    /// cycle; a row that is not refreshed expires on its own.
    /// </summary>
    Task RecordProjectRootAsync(string ownerId, string projectPath, DateTime nowUtc, TimeSpan timeToLive, CancellationToken cancellationToken = default);

    /// <summary>Removes the root's presence row when it stops cleanly.</summary>
    Task ReleaseProjectRootAsync(string ownerId, CancellationToken cancellationToken = default);

    /// <summary>The projects with at least one root whose presence has not expired at <paramref name="nowUtc"/>.</summary>
    Task<IReadOnlyList<string>> GetOpenProjectRootsAsync(DateTime nowUtc, CancellationToken cancellationToken = default);
    Task<int> FailStalledLaunchesAsync(TimeSpan grace, CancellationToken cancellationToken = default);
    Task<bool> StartRunAsync(string runId, int processId, CancellationToken cancellationToken = default);
    Task CompleteRunAsync(string runId, JobRunStatus status, int? exitCode, string? errorMessage, CancellationToken cancellationToken = default);

    /// <summary>
    /// Completes an idled run atomically, preferring pending cancellation and preserving earlier
    /// failed actions. Returns the durable outcome if another completion path already won.
    /// </summary>
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

