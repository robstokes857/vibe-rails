using System.Globalization;
using Microsoft.Data.Sqlite;
using VibeRails.DTOs;
using VibeRails.Services;
using VibeRails.Services.Jobs;

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
    Task<JobRunRecord?> GetRunAsync(string runId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<JobRunRecord>> GetQueuedRunsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<JobRunRecord>> GetActiveRunsAsync(CancellationToken cancellationToken = default);
    Task<int> CountRunningRunsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<JobRunRecord>> GetLaunchableRunsAsync(CancellationToken cancellationToken = default);
    Task<bool> TryMarkLaunchedAsync(string runId, CancellationToken cancellationToken = default);
    Task<int> FailStalledLaunchesAsync(TimeSpan grace, CancellationToken cancellationToken = default);
    Task<bool> StartRunAsync(string runId, int processId, CancellationToken cancellationToken = default);
    Task SetRunSessionAsync(string runId, string sessionId, CancellationToken cancellationToken = default);
    Task CompleteRunAsync(string runId, JobRunStatus status, int? exitCode, string? errorMessage, CancellationToken cancellationToken = default);
    Task<bool> RequestCancelAsync(string runId, CancellationToken cancellationToken = default);
    Task<bool> IsCancelRequestedAsync(string runId, CancellationToken cancellationToken = default);
}

public sealed class JobStore : IJobStore
{
    private readonly string _connectionString;

    public JobStore(string connectionString)
    {
        _connectionString = connectionString;
        EnsureSchema();
    }

    public async Task<IReadOnlyList<JobDefinitionRecord>> GetJobsAsync(
        string? projectPath = null,
        bool includeDeleted = false,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var jobs = new List<JobDefinitionRecord>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = JobSelectSql + "\n" + $"""
                WHERE ($includeDeleted = 1 OR j.DeletedUTC IS NULL)
                  AND ($projectPath IS NULL OR j.ProjectPath = $projectPath{ProjectPathCollation})
                ORDER BY j.Enabled DESC, j.UpdatedUTC DESC, j.Id DESC;
                """;
            command.Parameters.AddWithValue("$includeDeleted", includeDeleted ? 1 : 0);
            command.Parameters.AddWithValue("$projectPath", projectPath is null ? DBNull.Value : NormalizeProjectPath(projectPath));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                jobs.Add(ReadJob(reader));
        }

        var triggersByJob = await ReadTriggersForJobsAsync(connection, projectPath, includeDeleted, cancellationToken);
        for (var index = 0; index < jobs.Count; index++)
        {
            IReadOnlyList<JobTriggerDto> triggers = triggersByJob.TryGetValue(jobs[index].Id, out var jobTriggers) ? jobTriggers : [];
            jobs[index] = jobs[index] with { Triggers = triggers };
        }
        return jobs;
    }

    public async Task<JobDefinitionRecord?> GetJobAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        JobDefinitionRecord? job;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = JobSelectSql + " WHERE j.Id = $id LIMIT 1;";
            command.Parameters.AddWithValue("$id", id);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            job = await reader.ReadAsync(cancellationToken) ? ReadJob(reader) : null;
        }
        return job is null ? null : job with { Triggers = await ReadTriggersAsync(connection, job.Id, cancellationToken) };
    }

    public async Task<JobDefinitionRecord> CreateJobAsync(CreateJobRequest request, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO Jobs (Name, ProjectPath, EnvironmentId, TimeoutMinutes, Enabled, CreatedUTC, UpdatedUTC)
            VALUES ($name, $projectPath, $environmentId, $timeoutMinutes, $enabled, $now, $now)
            RETURNING Id;
            """;
        BindJob(command, request.Name, request.ProjectPath, request.EnvironmentId, request.TimeoutMinutes, request.Enabled, now);
        var id = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        await ReplaceTriggersAsync(connection, transaction, id, request.Triggers, now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (await GetJobAsync(id, cancellationToken))!;
    }

    public async Task<JobDefinitionRecord?> UpdateJobAsync(long id, UpdateJobRequest request, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE Jobs SET
                Name = $name, ProjectPath = $projectPath, EnvironmentId = $environmentId,
                TimeoutMinutes = $timeoutMinutes, Enabled = $enabled, UpdatedUTC = $now
            WHERE Id = $id AND DeletedUTC IS NULL;
            """;
        BindJob(command, request.Name, request.ProjectPath, request.EnvironmentId, request.TimeoutMinutes, request.Enabled, now);
        command.Parameters.AddWithValue("$id", id);
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        await ReplaceTriggersAsync(connection, transaction, id, request.Triggers, now, cancellationToken);
        if (!request.Enabled)
            await CancelQueuedRunsAsync(connection, transaction, id, "Automation disabled before the run started.", now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await GetJobAsync(id, cancellationToken);
    }

    public async Task<bool> SoftDeleteJobAsync(long id, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        int changed;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "UPDATE Jobs SET Enabled = 0, DeletedUTC = $now, UpdatedUTC = $now WHERE Id = $id AND DeletedUTC IS NULL;";
            command.Parameters.AddWithValue("$now", ToDb(now));
            command.Parameters.AddWithValue("$id", id);
            changed = await command.ExecuteNonQueryAsync(cancellationToken);
        }
        if (changed > 0)
            await CancelQueuedRunsAsync(connection, transaction, id, "Automation deleted before the run started.", now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return changed > 0;
    }

    public async Task<int> CountJobsForEnvironmentAsync(int environmentId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM Jobs WHERE EnvironmentId = $id AND DeletedUTC IS NULL;";
        command.Parameters.AddWithValue("$id", environmentId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    public async Task<bool> TryDeleteEnvironmentIfUnusedAsync(
        int environmentId,
        Action stageFilesystemDeletion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stageFilesystemDeletion);

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        try
        {
            await using (var eligibility = connection.CreateCommand())
            {
                eligibility.Transaction = transaction;
                eligibility.CommandText = """
                    SELECT EXISTS (
                        SELECT 1 FROM Environments e
                        WHERE e.Id = $id
                          AND NOT EXISTS (SELECT 1 FROM Jobs j WHERE j.EnvironmentId = e.Id AND j.DeletedUTC IS NULL)
                    );
                    """;
                eligibility.Parameters.AddWithValue("$id", environmentId);
                var canDelete = Convert.ToInt32(await eligibility.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) != 0;
                if (!canDelete)
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                    return false;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            stageFilesystemDeletion();

            await using (var delete = connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM Environments WHERE Id = $id;";
                delete.Parameters.AddWithValue("$id", environmentId);
                if (await delete.ExecuteNonQueryAsync(CancellationToken.None) != 1)
                    throw new InvalidOperationException($"Environment {environmentId} changed during its guarded deletion.");
            }

            await transaction.CommitAsync(CancellationToken.None);
            return true;
        }
        catch (Exception deleteException)
        {
            try
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
            catch (Exception rollbackException)
            {
                throw new EnvironmentDeleteRollbackException(environmentId, deleteException, rollbackException);
            }
            throw;
        }
    }

    public async Task<int> CountEnabledJobsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM Jobs WHERE Enabled = 1 AND DeletedUTC IS NULL;";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Atomically acquires the single scheduler lease, or renews it when this instance already owns
    /// it. A different owner may take over only after the stored UTC expiry has passed.
    /// </summary>
    public async Task<bool> TryAcquireOrRenewSchedulerLeaseAsync(
        string ownerId,
        DateTime nowUtc,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ownerId))
            throw new ArgumentException("A scheduler lease owner is required.", nameof(ownerId));
        if (leaseDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(leaseDuration), "The scheduler lease duration must be positive.");

        nowUtc = nowUtc.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc)
            : nowUtc.ToUniversalTime();
        var expiresUtc = nowUtc.Add(leaseDuration);

        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO JobSchedulerLease (LeaseName, OwnerId, ExpiresUTC)
            VALUES ($leaseName, $ownerId, $expiresUtc)
            ON CONFLICT(LeaseName) DO UPDATE SET
                OwnerId = excluded.OwnerId,
                ExpiresUTC = excluded.ExpiresUTC
            WHERE JobSchedulerLease.OwnerId = excluded.OwnerId
               OR JobSchedulerLease.ExpiresUTC <= $nowUtc;
            """;
        command.Parameters.AddWithValue("$leaseName", SchedulerLeaseName);
        command.Parameters.AddWithValue("$ownerId", ownerId);
        command.Parameters.AddWithValue("$nowUtc", ToDb(nowUtc));
        command.Parameters.AddWithValue("$expiresUtc", ToDb(expiresUtc));
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    /// <summary>
    /// Releases the scheduler lease only when it is still owned by the caller. A stale owner cannot
    /// delete a lease that another VibeRails instance acquired after expiry.
    /// </summary>
    public async Task<bool> ReleaseSchedulerLeaseAsync(
        string ownerId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ownerId))
            throw new ArgumentException("A scheduler lease owner is required.", nameof(ownerId));

        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM JobSchedulerLease
            WHERE LeaseName = $leaseName AND OwnerId = $ownerId;
            """;
        command.Parameters.AddWithValue("$leaseName", SchedulerLeaseName);
        command.Parameters.AddWithValue("$ownerId", ownerId);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public Task<string?> EnqueueManualRunAsync(long jobId, CancellationToken cancellationToken = default) =>
        EnqueueJobRunAsync(jobId, JobTriggerKind.Manual, $"manual:{Guid.NewGuid():N}", requireEnabled: false, cancellationToken);

    public async Task<string?> EnqueueRetryAsync(string runId, CancellationToken cancellationToken = default)
    {
        // A retry is just a fresh manual run of the same job. The recorded session of the source run
        // is kept; the retry produces its own new session.
        var source = await GetRunAsync(runId, cancellationToken);
        if (source is null)
            return null;
        return await EnqueueJobRunAsync(source.JobId, JobTriggerKind.Manual, $"retry:{runId}:{Guid.NewGuid():N}", requireEnabled: false, cancellationToken);
    }

    public async Task<IReadOnlyList<string>> EnqueueEventRunsAsync(
        string projectPath,
        JobTriggerKind kind,
        string eventKey,
        CancellationToken cancellationToken = default)
    {
        if (kind != JobTriggerKind.Commit)
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Only Commit event runs are supported.");

        await using var connection = await OpenAsync(cancellationToken);
        var jobIds = new List<long>();
        await using (var query = connection.CreateCommand())
        {
            query.CommandText = $"""
                SELECT j.Id
                FROM Jobs j
                JOIN JobTriggers t ON t.JobId = j.Id AND t.Kind = $kind
                WHERE j.Enabled = 1 AND j.DeletedUTC IS NULL AND j.ProjectPath = $projectPath{ProjectPathCollation};
                """;
            query.Parameters.AddWithValue("$kind", (int)kind);
            query.Parameters.AddWithValue("$projectPath", NormalizeProjectPath(projectPath));
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                jobIds.Add(reader.GetInt64(0));
        }

        var runIds = new List<string>(jobIds.Count);
        foreach (var jobId in jobIds)
        {
            var runId = await EnqueueJobRunAsync(jobId, kind, $"commit:{jobId}:{eventKey}", requireEnabled: true, cancellationToken);
            if (runId != null)
                runIds.Add(runId);
        }
        return runIds;
    }

    public async Task<IReadOnlyList<string>> EnqueueDueSchedulesAsync(DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        nowUtc = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc);
        await using var connection = await OpenAsync(cancellationToken);
        var due = new List<(long JobId, long TriggerId, DateTime ScheduledUtc, JobTriggerRequest Trigger)>();
        await using (var query = connection.CreateCommand())
        {
            query.CommandText = """
                SELECT j.Id, t.Id, t.NextRunUTC, t.ScheduleKind, t.IntervalMinutes,
                       t.LocalTime, t.DaysOfWeekMask, t.TimeZoneId
                FROM Jobs j
                JOIN JobTriggers t ON t.JobId = j.Id
                WHERE j.Enabled = 1 AND j.DeletedUTC IS NULL
                  AND t.Kind = $schedule AND t.NextRunUTC IS NOT NULL AND t.NextRunUTC <= $now
                ORDER BY t.NextRunUTC, t.Id;
                """;
            query.Parameters.AddWithValue("$schedule", (int)JobTriggerKind.Schedule);
            query.Parameters.AddWithValue("$now", ToDb(nowUtc));
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                due.Add((
                    reader.GetInt64(0),
                    reader.GetInt64(1),
                    ParseDb(reader.GetString(2)),
                    new JobTriggerRequest(
                        JobTriggerKind.Schedule,
                        (JobScheduleKind)reader.GetInt32(3),
                        reader.IsDBNull(4) ? null : reader.GetInt32(4),
                        reader.IsDBNull(5) ? null : reader.GetString(5),
                        reader.GetInt32(6),
                        reader.IsDBNull(7) ? null : reader.GetString(7))));
            }
        }

        var runIds = new List<string>();
        foreach (var item in due)
        {
            DateTime next;
            try
            {
                next = JobScheduleCalculator.ComputeNext(item.Trigger, nowUtc);
            }
            catch
            {
                // Unresolvable schedule (e.g. a dropped time zone): clear NextRunUTC so it stops being
                // due instead of throwing on every tick. A later job edit recomputes it.
                await using var disable = connection.CreateCommand();
                disable.CommandText = "UPDATE JobTriggers SET LastRunUTC = NextRunUTC, NextRunUTC = NULL WHERE Id = $id AND NextRunUTC = $expected;";
                disable.Parameters.AddWithValue("$id", item.TriggerId);
                disable.Parameters.AddWithValue("$expected", ToDb(item.ScheduledUtc));
                await disable.ExecuteNonQueryAsync(cancellationToken);
                continue;
            }

            await using var transaction = connection.BeginTransaction(deferred: false);
            await using var advance = connection.CreateCommand();
            advance.Transaction = transaction;
            advance.CommandText = "UPDATE JobTriggers SET LastRunUTC = NextRunUTC, NextRunUTC = $next WHERE Id = $id AND NextRunUTC = $expected;";
            advance.Parameters.AddWithValue("$next", ToDb(next));
            advance.Parameters.AddWithValue("$id", item.TriggerId);
            advance.Parameters.AddWithValue("$expected", ToDb(item.ScheduledUtc));
            if (await advance.ExecuteNonQueryAsync(cancellationToken) == 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                continue;
            }

            var runId = await InsertRunAsync(connection, transaction, item.JobId, JobTriggerKind.Schedule,
                $"schedule:{item.TriggerId}:{ToDb(item.ScheduledUtc)}", requireEnabled: true, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            if (runId != null)
                runIds.Add(runId);
        }
        return runIds;
    }

    public async Task<IReadOnlyList<JobRunRecord>> GetRunsAsync(long? jobId = null, int limit = 100, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var results = new List<JobRunRecord>();
        await using var command = connection.CreateCommand();
        command.CommandText = RunSelectSql + "\n" + """
            WHERE ($jobId IS NULL OR r.JobId = $jobId)
            ORDER BY r.QueuedUTC DESC, r.Id DESC LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$jobId", jobId is null ? DBNull.Value : jobId.Value);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) results.Add(ReadRun(reader));
        return results;
    }

    public async Task<JobRunRecord?> GetRunAsync(string runId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = RunSelectSql + " WHERE r.Id = $id LIMIT 1;";
        command.Parameters.AddWithValue("$id", runId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadRun(reader) : null;
    }

    /// <summary>
    /// Runs with a terminal currently open. Backs the machine-wide cap: the per-job overlap guard
    /// stops one job stacking windows, this stops many jobs each legitimately opening one at the
    /// same moment.
    ///
    /// Counts Running only, never Queued — queued runs have no terminal yet, and including them
    /// would let a tick compare against a number that already contains the runs it is about to
    /// launch, so the cap would be reached without a single window being open.
    /// </summary>
    public async Task<int> CountRunningRunsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM JobRuns WHERE Status = $running;";
        command.Parameters.AddWithValue("$running", (int)JobRunStatus.Running);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Queued runs whose terminal has not been spawned yet. A run stays Queued until the launched
    /// `vb --job-run` process claims it via <see cref="StartRunAsync"/>, so LaunchedUTC — not
    /// Status — is what stops a second tick from opening a second window for the same run.
    /// </summary>
    public async Task<IReadOnlyList<JobRunRecord>> GetLaunchableRunsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var results = new List<JobRunRecord>();
        await using var command = connection.CreateCommand();
        command.CommandText = RunSelectSql + """
             WHERE r.Status = $queued AND r.LaunchedUTC IS NULL AND r.CancelRequested = 0
             ORDER BY r.QueuedUTC;
            """;
        command.Parameters.AddWithValue("$queued", (int)JobRunStatus.Queued);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) results.Add(ReadRun(reader));
        return results;
    }

    /// <summary>
    /// Atomically claims the right to spawn this run's terminal. The shared scheduler lease avoids
    /// normal contention; this remains the final duplicate barrier during a stale lease handoff.
    /// </summary>
    public async Task<bool> TryMarkLaunchedAsync(string runId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE JobRuns SET LaunchedUTC = $now
            WHERE Id = $id AND Status = $queued AND LaunchedUTC IS NULL AND CancelRequested = 0;
            """;
        command.Parameters.AddWithValue("$now", ToDb(DateTime.UtcNow));
        command.Parameters.AddWithValue("$id", runId);
        command.Parameters.AddWithValue("$queued", (int)JobRunStatus.Queued);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    /// <summary>
    /// Fails runs whose terminal was spawned but never claimed the run. This is the detector for a
    /// launch that silently went nowhere — for example, when the native terminal launcher cannot
    /// reach an interactive desktop. Surfacing it as a failed run with a message beats silence.
    /// </summary>
    public async Task<int> FailStalledLaunchesAsync(TimeSpan grace, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE JobRuns
            SET Status = $failed, EndedUTC = $now,
                ErrorMessage = 'The terminal was launched but never started the run. If this keeps happening, the native terminal launcher may not have access to an interactive desktop.'
            WHERE Status = $queued AND LaunchedUTC IS NOT NULL AND LaunchedUTC <= $cutoff;
            """;
        command.Parameters.AddWithValue("$failed", (int)JobRunStatus.Failed);
        command.Parameters.AddWithValue("$queued", (int)JobRunStatus.Queued);
        command.Parameters.AddWithValue("$now", ToDb(DateTime.UtcNow));
        command.Parameters.AddWithValue("$cutoff", ToDb(DateTime.UtcNow - grace));
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public Task<IReadOnlyList<JobRunRecord>> GetQueuedRunsAsync(CancellationToken cancellationToken = default) =>
        GetRunsByStatusAsync(JobRunStatus.Queued, cancellationToken);

    public Task<IReadOnlyList<JobRunRecord>> GetActiveRunsAsync(CancellationToken cancellationToken = default) =>
        GetRunsByStatusAsync(JobRunStatus.Running, cancellationToken);

    private async Task<IReadOnlyList<JobRunRecord>> GetRunsByStatusAsync(JobRunStatus status, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var results = new List<JobRunRecord>();
        await using var command = connection.CreateCommand();
        command.CommandText = RunSelectSql + " WHERE r.Status = $status ORDER BY r.QueuedUTC;";
        command.Parameters.AddWithValue("$status", (int)status);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) results.Add(ReadRun(reader));
        return results;
    }

    public async Task<bool> StartRunAsync(string runId, int processId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        // Atomic claim: only a still-Queued, not-cancelled run flips to Running. Whoever wins this
        // gets to execute; a second spawner of the same run sees 0 rows and bows out.
        command.CommandText = """
            UPDATE JobRuns SET Status = $running, StartedUTC = $now, OwnerProcessId = $pid
            WHERE Id = $id AND Status = $queued AND CancelRequested = 0;
            """;
        command.Parameters.AddWithValue("$running", (int)JobRunStatus.Running);
        command.Parameters.AddWithValue("$queued", (int)JobRunStatus.Queued);
        command.Parameters.AddWithValue("$now", ToDb(DateTime.UtcNow));
        command.Parameters.AddWithValue("$pid", processId);
        command.Parameters.AddWithValue("$id", runId);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task SetRunSessionAsync(string runId, string sessionId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE JobRuns SET SessionId = $sessionId WHERE Id = $id;";
        command.Parameters.AddWithValue("$sessionId", sessionId);
        command.Parameters.AddWithValue("$id", runId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task CompleteRunAsync(string runId, JobRunStatus status, int? exitCode, string? errorMessage, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        // Only finalize a run that is still Running (or Queued, for a never-started reap). This makes
        // completion idempotent and prevents a stale process from overwriting a terminal status.
        command.CommandText = """
            UPDATE JobRuns SET Status = $status, EndedUTC = $ended, ExitCode = $exitCode, ErrorMessage = $errorMessage
            WHERE Id = $id AND Status IN ($running, $queued);
            """;
        command.Parameters.AddWithValue("$status", (int)status);
        command.Parameters.AddWithValue("$running", (int)JobRunStatus.Running);
        command.Parameters.AddWithValue("$queued", (int)JobRunStatus.Queued);
        command.Parameters.AddWithValue("$ended", ToDb(DateTime.UtcNow));
        command.Parameters.AddWithValue("$exitCode", exitCode is null ? DBNull.Value : exitCode.Value);
        command.Parameters.AddWithValue("$errorMessage", errorMessage is null ? DBNull.Value : errorMessage);
        command.Parameters.AddWithValue("$id", runId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> RequestCancelAsync(string runId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE JobRuns SET
                CancelRequested = 1,
                Status = CASE WHEN Status = $queued THEN $cancelled ELSE Status END,
                EndedUTC = CASE WHEN Status = $queued THEN $now ELSE EndedUTC END,
                ErrorMessage = CASE WHEN Status = $queued THEN 'Cancelled before start.' ELSE ErrorMessage END
            WHERE Id = $id AND Status IN ($queued, $running);
            """;
        command.Parameters.AddWithValue("$queued", (int)JobRunStatus.Queued);
        command.Parameters.AddWithValue("$running", (int)JobRunStatus.Running);
        command.Parameters.AddWithValue("$cancelled", (int)JobRunStatus.Cancelled);
        command.Parameters.AddWithValue("$now", ToDb(DateTime.UtcNow));
        command.Parameters.AddWithValue("$id", runId);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<bool> IsCancelRequestedAsync(string runId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT CancelRequested FROM JobRuns WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", runId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken) ?? 0, CultureInfo.InvariantCulture) != 0;
    }

    private static async Task CancelQueuedRunsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long jobId,
        string reason,
        DateTime now,
        CancellationToken cancellationToken)
    {
        await using var cancel = connection.CreateCommand();
        cancel.Transaction = transaction;
        cancel.CommandText = """
            UPDATE JobRuns SET Status = $cancelled, EndedUTC = $now, ErrorMessage = $reason
            WHERE JobId = $id AND Status = $queued;
            """;
        cancel.Parameters.AddWithValue("$cancelled", (int)JobRunStatus.Cancelled);
        cancel.Parameters.AddWithValue("$queued", (int)JobRunStatus.Queued);
        cancel.Parameters.AddWithValue("$now", ToDb(now));
        cancel.Parameters.AddWithValue("$reason", reason);
        cancel.Parameters.AddWithValue("$id", jobId);
        await cancel.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<string?> EnqueueJobRunAsync(long jobId, JobTriggerKind kind, string triggerKey, bool requireEnabled, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var runId = await InsertRunAsync(connection, transaction, jobId, kind, triggerKey, requireEnabled, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return runId;
    }

    private static async Task<string?> InsertRunAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long jobId,
        JobTriggerKind kind,
        string triggerKey,
        bool requireEnabled,
        CancellationToken cancellationToken)
    {
        var runId = Guid.NewGuid().ToString("N");
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // Denormalized snapshot from the Job + its Environment. INNER JOIN Environments: a Job with
        // no live worker cannot be launched, so it simply produces no run (0 rows -> null).
        //
        // The NOT EXISTS clause is the self-overlap guard, and it lives here rather than in any
        // caller so that EVERY trigger path inherits it — schedule, commit, manual, and retry all
        // funnel through this statement. Putting it in the scheduler would leave the commit path
        // (which spawns straight from the git hook) uncapped. Because timeouts are opt-in, a run
        // can legitimately live for hours; without this a 10-minute schedule on a long job would
        // stack a new terminal window every 10 minutes forever.
        //
        // Evaluated inside the caller's transaction, so two concurrent enqueues of the same job
        // cannot both see "no active run" and both insert.
        command.CommandText = """
            INSERT OR IGNORE INTO JobRuns
                (Id, JobId, TriggerKind, TriggerKey, Status, JobName, ProjectPath, Llm,
                 EnvironmentId, EnvironmentName, TimeoutMinutes, QueuedUTC)
            SELECT $runId, j.Id, $triggerKind, $triggerKey, $queued, j.Name, j.ProjectPath,
                   e.LLM, j.EnvironmentId, e.CustomName, j.TimeoutMinutes, $queuedUtc
            FROM Jobs j
            JOIN Environments e ON e.Id = j.EnvironmentId
            WHERE j.Id = $jobId AND ($requireEnabled = 0 OR j.Enabled = 1) AND j.DeletedUTC IS NULL
              AND NOT EXISTS (
                  SELECT 1 FROM JobRuns active
                  WHERE active.JobId = j.Id AND active.Status IN ($queued, $running));
            """;
        command.Parameters.AddWithValue("$runId", runId);
        command.Parameters.AddWithValue("$jobId", jobId);
        command.Parameters.AddWithValue("$triggerKind", (int)kind);
        command.Parameters.AddWithValue("$triggerKey", triggerKey);
        command.Parameters.AddWithValue("$queued", (int)JobRunStatus.Queued);
        command.Parameters.AddWithValue("$running", (int)JobRunStatus.Running);
        command.Parameters.AddWithValue("$queuedUtc", ToDb(DateTime.UtcNow));
        command.Parameters.AddWithValue("$requireEnabled", requireEnabled ? 1 : 0);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0 ? runId : null;
    }

    private static async Task ReplaceTriggersAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long jobId,
        IReadOnlyList<JobTriggerRequest> triggers,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        // Snapshot current triggers so editing an unrelated field doesn't silently re-arm an
        // unchanged schedule to "now". UNIQUE(JobId, Kind) => a same-Kind prior row is the match.
        var existing = await ReadTriggersAsync(connection, jobId, cancellationToken);

        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM JobTriggers WHERE JobId = $jobId;";
            delete.Parameters.AddWithValue("$jobId", jobId);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }
        foreach (var trigger in triggers)
        {
            var prior = existing.FirstOrDefault(t => t.Kind == trigger.Kind);
            DateTime? next;
            if (trigger.Kind == JobTriggerKind.Schedule)
            {
                var unchanged = prior is not null
                    && prior.ScheduleKind == trigger.ScheduleKind
                    && prior.IntervalMinutes == trigger.IntervalMinutes
                    && prior.LocalTime == trigger.LocalTime
                    && prior.DaysOfWeekMask == trigger.DaysOfWeekMask
                    && prior.TimeZoneId == trigger.TimeZoneId;
                next = unchanged && prior!.NextRunUtc is not null
                    ? prior.NextRunUtc
                    : JobScheduleCalculator.ComputeNext(trigger, nowUtc);
            }
            else
            {
                next = null;
            }

            var lastRun = prior?.LastRunUtc;

            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO JobTriggers
                    (JobId, Kind, ScheduleKind, IntervalMinutes, LocalTime, DaysOfWeekMask, TimeZoneId, NextRunUTC, LastRunUTC)
                VALUES
                    ($jobId, $kind, $scheduleKind, $intervalMinutes, $localTime, $daysMask, $timeZone, $nextRun, $lastRun);
                """;
            insert.Parameters.AddWithValue("$jobId", jobId);
            insert.Parameters.AddWithValue("$kind", (int)trigger.Kind);
            insert.Parameters.AddWithValue("$scheduleKind", trigger.ScheduleKind is null ? DBNull.Value : (int)trigger.ScheduleKind.Value);
            insert.Parameters.AddWithValue("$intervalMinutes", trigger.IntervalMinutes is null ? DBNull.Value : trigger.IntervalMinutes.Value);
            insert.Parameters.AddWithValue("$localTime", trigger.LocalTime is null ? DBNull.Value : trigger.LocalTime);
            insert.Parameters.AddWithValue("$daysMask", trigger.DaysOfWeekMask);
            insert.Parameters.AddWithValue("$timeZone", trigger.TimeZoneId is null ? DBNull.Value : trigger.TimeZoneId);
            insert.Parameters.AddWithValue("$nextRun", next is null ? DBNull.Value : ToDb(next.Value));
            insert.Parameters.AddWithValue("$lastRun", lastRun is null ? DBNull.Value : ToDb(lastRun.Value));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static void BindJob(SqliteCommand command, string name, string projectPath, int? environmentId, int? timeoutMinutes, bool enabled, DateTime now)
    {
        command.Parameters.AddWithValue("$name", name.Trim());
        command.Parameters.AddWithValue("$projectPath", NormalizeProjectPath(projectPath));
        command.Parameters.AddWithValue("$environmentId", environmentId is null ? DBNull.Value : environmentId.Value);
        // No timeout is stored as 0; see ToOptionalTimeout.
        command.Parameters.AddWithValue("$timeoutMinutes", timeoutMinutes is > 0 ? timeoutMinutes.Value : 0);
        command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
        command.Parameters.AddWithValue("$now", ToDb(now));
    }

    private static JobDefinitionRecord ReadJob(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.IsDBNull(3) ? LLM.NotSet : (LLM)reader.GetInt32(3),
        reader.IsDBNull(4) ? null : reader.GetInt32(4),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.IsDBNull(6) ? "" : reader.GetString(6),
        ToOptionalTimeout(reader.GetInt32(7)),
        reader.GetInt32(8) != 0,
        ParseDb(reader.GetString(9)),
        ParseDb(reader.GetString(10)),
        reader.IsDBNull(11) ? null : ParseDb(reader.GetString(11)),
        []);

    private static async Task<IReadOnlyList<JobTriggerDto>> ReadTriggersAsync(SqliteConnection connection, long jobId, CancellationToken cancellationToken)
    {
        var triggers = new List<JobTriggerDto>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Kind, ScheduleKind, IntervalMinutes, LocalTime, DaysOfWeekMask, TimeZoneId, NextRunUTC, LastRunUTC
            FROM JobTriggers WHERE JobId = $jobId ORDER BY Kind, Id;
            """;
        command.Parameters.AddWithValue("$jobId", jobId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            triggers.Add(ReadTrigger(reader));
        return triggers;
    }

    private static async Task<Dictionary<long, List<JobTriggerDto>>> ReadTriggersForJobsAsync(
        SqliteConnection connection,
        string? projectPath,
        bool includeDeleted,
        CancellationToken cancellationToken)
    {
        var triggersByJob = new Dictionary<long, List<JobTriggerDto>>();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT t.JobId, t.Id, t.Kind, t.ScheduleKind, t.IntervalMinutes, t.LocalTime, t.DaysOfWeekMask, t.TimeZoneId, t.NextRunUTC, t.LastRunUTC
            FROM JobTriggers t
            JOIN Jobs j ON j.Id = t.JobId
            WHERE ($includeDeleted = 1 OR j.DeletedUTC IS NULL)
              AND ($projectPath IS NULL OR j.ProjectPath = $projectPath{ProjectPathCollation})
            ORDER BY t.JobId, t.Kind, t.Id;
            """;
        command.Parameters.AddWithValue("$includeDeleted", includeDeleted ? 1 : 0);
        command.Parameters.AddWithValue("$projectPath", projectPath is null ? DBNull.Value : NormalizeProjectPath(projectPath));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var jobId = reader.GetInt64(0);
            if (!triggersByJob.TryGetValue(jobId, out var triggers))
            {
                triggers = [];
                triggersByJob.Add(jobId, triggers);
            }
            triggers.Add(ReadTrigger(reader, offset: 1));
        }
        return triggersByJob;
    }

    private static JobTriggerDto ReadTrigger(SqliteDataReader reader, int offset = 0) => new(
        reader.GetInt64(offset),
        (JobTriggerKind)reader.GetInt32(offset + 1),
        reader.IsDBNull(offset + 2) ? null : (JobScheduleKind)reader.GetInt32(offset + 2),
        reader.IsDBNull(offset + 3) ? null : reader.GetInt32(offset + 3),
        reader.IsDBNull(offset + 4) ? null : reader.GetString(offset + 4),
        reader.GetInt32(offset + 5),
        reader.IsDBNull(offset + 6) ? null : reader.GetString(offset + 6),
        reader.IsDBNull(offset + 7) ? null : ParseDb(reader.GetString(offset + 7)),
        reader.IsDBNull(offset + 8) ? null : ParseDb(reader.GetString(offset + 8)));

    private static JobRunRecord ReadRun(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetInt64(1),
        (JobTriggerKind)reader.GetInt32(2),
        reader.GetString(3),
        (JobRunStatus)reader.GetInt32(4),
        reader.GetString(5),
        reader.GetString(6),
        (LLM)reader.GetInt32(7),
        reader.IsDBNull(8) ? null : reader.GetInt32(8),
        reader.IsDBNull(9) ? null : reader.GetString(9),
        ToOptionalTimeout(reader.GetInt32(10)),
        reader.IsDBNull(11) ? null : reader.GetString(11),
        ParseDb(reader.GetString(12)),
        reader.IsDBNull(13) ? null : ParseDb(reader.GetString(13)),
        reader.IsDBNull(14) ? null : ParseDb(reader.GetString(14)),
        reader.IsDBNull(15) ? null : reader.GetInt32(15),
        reader.IsDBNull(16) ? null : reader.GetString(16),
        reader.GetInt32(17) != 0,
        reader.IsDBNull(18) ? null : reader.GetInt32(18));

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        await command.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    private void EnsureSchema()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        // The previous Jobs implementation (never shipped as used) left two tables behind that
        // nothing reads any more. Dropping them is unconditional because there is no case where
        // keeping them is right and no data in them worth a migration.
        using (var drop = connection.CreateCommand())
        {
            drop.CommandText = """
                DROP TABLE IF EXISTS JobRunLogs;
                DROP TABLE IF EXISTS JobWorkerLease;
                """;
            drop.ExecuteNonQuery();
        }

        // That old implementation also shaped Jobs/JobTriggers/JobRuns differently, so those have to
        // be rebuilt rather than kept. The trigger is the removed ExecutionMode column — a marker
        // that only the old schema can have. Detecting it by the presence of a *table* name instead
        // would be a live hazard: any future feature that reintroduced a table called JobRunLogs
        // would silently drop every real Job on the machine.
        if (HasColumn(connection, "Jobs", "ExecutionMode"))
        {
            using var dropLegacyJobs = connection.CreateCommand();
            dropLegacyJobs.CommandText = """
                DROP TABLE IF EXISTS JobRuns;
                DROP TABLE IF EXISTS JobTriggers;
                DROP TABLE IF EXISTS Jobs;
                """;
            dropLegacyJobs.ExecuteNonQuery();
        }

        using var command = connection.CreateCommand();
        command.CommandText = SchemaSql;
        command.ExecuteNonQuery();

        // Column adds for databases created before the column existed — CREATE TABLE IF NOT EXISTS
        // won't alter an existing table. Guarded by an explicit column check rather than running the
        // ALTER and swallowing the failure: SQLITE_ERROR is the generic code and also covers "no
        // such table" and similar, so catching it would hide a real schema problem as a no-op.
        foreach (var (table, column, definition) in new[] { ("JobRuns", "LaunchedUTC", "TEXT") })
        {
            if (HasColumn(connection, table, column))
                continue;

            using var alter = connection.CreateCommand();
            alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition}";
            alter.ExecuteNonQuery();
        }
    }

    private static bool HasColumn(SqliteConnection connection, string table, string column)
    {
        using var command = connection.CreateCommand();
        // PRAGMA returns no rows at all for a table that doesn't exist, which is the answer we want.
        command.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = $column;";
        command.Parameters.AddWithValue("$column", column);
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
    }

    // Windows filesystems compare paths case-insensitively; Linux does not. Project paths keep their
    // original casing; case-insensitive matching on Windows is applied at the comparison sites.
    private static readonly string ProjectPathCollation = OperatingSystem.IsWindows() ? " COLLATE NOCASE" : string.Empty;
    private const string SchedulerLeaseName = "automation-scheduler";

    public static string NormalizeProjectPath(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));

    // 0 is the "no timeout" sentinel in SQLite because the column is NOT NULL and predates the
    // opt-in behaviour; a nullable column would have required rebuilding the table.
    private static int? ToOptionalTimeout(int stored) => stored > 0 ? stored : null;

    private static string ToDb(DateTime value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTime ParseDb(string value) => DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();

    private const string JobSelectSql = """
        SELECT j.Id, j.Name, j.ProjectPath, e.LLM, j.EnvironmentId, e.CustomName,
               COALESCE(NULLIF(TRIM(e.CustomPrompt), ''), ''), j.TimeoutMinutes, j.Enabled,
               j.CreatedUTC, j.UpdatedUTC, j.DeletedUTC
        FROM Jobs j LEFT JOIN Environments e ON e.Id = j.EnvironmentId
        """;

    private const string RunSelectSql = """
        SELECT r.Id, r.JobId, r.TriggerKind, r.TriggerKey, r.Status, r.JobName, r.ProjectPath,
               r.Llm, r.EnvironmentId, r.EnvironmentName, r.TimeoutMinutes, r.SessionId,
               r.QueuedUTC, r.StartedUTC, r.EndedUTC, r.ExitCode, r.ErrorMessage,
               r.CancelRequested, r.OwnerProcessId
        FROM JobRuns r
        """;

    private const string SchemaSql = """
        PRAGMA busy_timeout=5000;
        PRAGMA journal_mode=WAL;
        PRAGMA foreign_keys=ON;

        CREATE TABLE IF NOT EXISTS Jobs (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            Name TEXT NOT NULL,
            ProjectPath TEXT NOT NULL,
            EnvironmentId INTEGER,
            TimeoutMinutes INTEGER NOT NULL DEFAULT 60,
            Enabled INTEGER NOT NULL DEFAULT 0,
            CreatedUTC TEXT NOT NULL,
            UpdatedUTC TEXT NOT NULL,
            DeletedUTC TEXT,
            FOREIGN KEY (EnvironmentId) REFERENCES Environments(Id) ON DELETE SET NULL
        );

        CREATE TABLE IF NOT EXISTS JobTriggers (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            JobId INTEGER NOT NULL,
            Kind INTEGER NOT NULL,
            ScheduleKind INTEGER,
            IntervalMinutes INTEGER,
            LocalTime TEXT,
            DaysOfWeekMask INTEGER NOT NULL DEFAULT 0,
            TimeZoneId TEXT,
            NextRunUTC TEXT,
            LastRunUTC TEXT,
            FOREIGN KEY (JobId) REFERENCES Jobs(Id),
            UNIQUE(JobId, Kind)
        );

        CREATE TABLE IF NOT EXISTS JobRuns (
            Id TEXT PRIMARY KEY,
            JobId INTEGER NOT NULL,
            TriggerKind INTEGER NOT NULL,
            TriggerKey TEXT NOT NULL UNIQUE,
            Status INTEGER NOT NULL,
            JobName TEXT NOT NULL,
            ProjectPath TEXT NOT NULL,
            Llm INTEGER NOT NULL,
            EnvironmentId INTEGER,
            EnvironmentName TEXT,
            TimeoutMinutes INTEGER NOT NULL,
            SessionId TEXT,
            QueuedUTC TEXT NOT NULL,
            StartedUTC TEXT,
            EndedUTC TEXT,
            ExitCode INTEGER,
            ErrorMessage TEXT,
            CancelRequested INTEGER NOT NULL DEFAULT 0,
            OwnerProcessId INTEGER,
            LaunchedUTC TEXT,
            FOREIGN KEY (JobId) REFERENCES Jobs(Id)
        );

        CREATE TABLE IF NOT EXISTS JobSchedulerLease (
            LeaseName TEXT PRIMARY KEY,
            OwnerId TEXT NOT NULL,
            ExpiresUTC TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS idx_jobs_project ON Jobs(ProjectPath, Enabled, DeletedUTC);
        CREATE INDEX IF NOT EXISTS idx_job_triggers_due ON JobTriggers(Kind, NextRunUTC);
        CREATE INDEX IF NOT EXISTS idx_job_runs_queue ON JobRuns(Status, QueuedUTC);
        CREATE INDEX IF NOT EXISTS idx_job_runs_job ON JobRuns(JobId, QueuedUTC DESC);
        """;
}

public sealed class EnvironmentDeleteRollbackException : Exception
{
    internal EnvironmentDeleteRollbackException(int environmentId, Exception deleteException, Exception rollbackException)
        : base($"Could not confirm rollback while deleting environment {environmentId}.", new AggregateException(deleteException, rollbackException))
    {
    }
}
