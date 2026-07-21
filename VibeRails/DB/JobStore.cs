using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Serilog;
using VibeRails.DTOs;
using VibeRails.Services;
using VibeRails.Services.Jobs;

namespace VibeRails.DB;

public interface IJobStore
{
    Task<IReadOnlyList<JobDefinitionRecord>> GetJobsAsync(string? projectPath = null, bool includeDeleted = false, CancellationToken cancellationToken = default);
    Task<JobDefinitionRecord?> GetJobAsync(long id, CancellationToken cancellationToken = default);
    Task<JobDefinitionRecord> CreateJobAsync(CreateJobRequest request, string? executablePath, CancellationToken cancellationToken = default);
    Task<JobDefinitionRecord?> UpdateJobAsync(long id, UpdateJobRequest request, string? executablePath, CancellationToken cancellationToken = default);
    Task<bool> SoftDeleteJobAsync(long id, CancellationToken cancellationToken = default);
    Task<int> CountEnabledJobsAsync(CancellationToken cancellationToken = default);
    Task<int> CountJobsForEnvironmentAsync(int environmentId, CancellationToken cancellationToken = default);
    Task<bool> TryDeleteEnvironmentIfUnusedAsync(
        int environmentId,
        Action stageFilesystemDeletion,
        CancellationToken cancellationToken = default);
    Task<string?> EnqueueManualRunAsync(long jobId, string? contextJson = null, CancellationToken cancellationToken = default);
    Task<string?> EnqueueRetryAsync(string runId, string? contextJsonOverride = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> EnqueueEventRunsAsync(string projectPath, JobTriggerKind kind, string eventKey, string? contextJson, CancellationToken cancellationToken = default);
    Task<int> EnqueueDueSchedulesAsync(DateTime nowUtc, CancellationToken cancellationToken = default);
    Task<JobRunRecord?> ClaimNextRunAsync(string instanceId, int processId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<JobRunRecord>> GetRunsAsync(long? jobId = null, int limit = 100, CancellationToken cancellationToken = default);
    Task<JobRunRecord?> GetRunAsync(string runId, CancellationToken cancellationToken = default);
    Task<JobSnapshotArtifactReferences> GetActiveSnapshotArtifactReferencesAsync(CancellationToken cancellationToken = default);
    Task AppendRunLogAsync(string runId, long sequence, string stream, string content, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<JobRunLogResponse>> GetRunLogsAsync(string runId, long afterSequence, int limit = 1000, CancellationToken cancellationToken = default);
    Task CompleteRunAsync(string runId, JobRunStatus status, int? exitCode, string? resultText, string? errorMessage, string? expectedOwnerInstanceId = null, CancellationToken cancellationToken = default);
    Task SetRunWorkspaceAsync(string runId, string? workspacePath, CancellationToken cancellationToken = default);
    Task<bool> RequestCancelAsync(string runId, CancellationToken cancellationToken = default);
    Task<bool> IsCancelRequestedAsync(string runId, CancellationToken cancellationToken = default);
    Task<int> MarkRunningRunsInterruptedAsync(string reason, CancellationToken cancellationToken = default);
    Task<bool> TryAcquireWorkerLeaseAsync(JobWorkerLeaseRecord lease, DateTime staleBeforeUtc, CancellationToken cancellationToken = default);
    Task UpsertWorkerLeaseAsync(JobWorkerLeaseRecord lease, CancellationToken cancellationToken = default);
    Task<JobWorkerLeaseRecord?> GetWorkerLeaseAsync(CancellationToken cancellationToken = default);
    Task DeleteWorkerLeaseAsync(string instanceId, CancellationToken cancellationToken = default);
    Task<(int Definitions, int Runs)> GetLegacyCronCountsAsync(CancellationToken cancellationToken = default);
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

        var triggersByJob = await ReadTriggersForJobsAsync(
            connection,
            projectPath,
            includeDeleted,
            cancellationToken);
        for (var index = 0; index < jobs.Count; index++)
        {
            IReadOnlyList<JobTriggerDto> triggers = triggersByJob.TryGetValue(jobs[index].Id, out var jobTriggers)
                ? jobTriggers
                : [];
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

        return job is null
            ? null
            : job with { Triggers = await ReadTriggersAsync(connection, job.Id, cancellationToken) };
    }

    public async Task<JobDefinitionRecord> CreateJobAsync(
        CreateJobRequest request,
        string? executablePath,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO Jobs
                (Name, ProjectPath, Llm, EnvironmentId, Prompt, ExecutionMode, TimeoutMinutes,
                 Enabled, ExecutablePath, CreatedUTC, UpdatedUTC)
            VALUES
                ($name, $projectPath, $llm, $environmentId, $prompt, $executionMode, $timeoutMinutes,
                 $enabled, $executablePath, $now, $now)
            RETURNING Id;
            """;
        BindJob(command, request.Name, request.ProjectPath, request.Llm, request.EnvironmentId,
            request.Prompt, request.ExecutionMode, request.TimeoutMinutes, request.Enabled, executablePath, now);
        var id = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        await ReplaceTriggersAsync(connection, transaction, id, request.Triggers, now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (await GetJobAsync(id, cancellationToken))!;
    }

    public async Task<JobDefinitionRecord?> UpdateJobAsync(
        long id,
        UpdateJobRequest request,
        string? executablePath,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE Jobs SET
                Name = $name,
                ProjectPath = $projectPath,
                Llm = $llm,
                EnvironmentId = $environmentId,
                Prompt = $prompt,
                ExecutionMode = $executionMode,
                TimeoutMinutes = $timeoutMinutes,
                Enabled = $enabled,
                ExecutablePath = $executablePath,
                UpdatedUTC = $now
            WHERE Id = $id AND DeletedUTC IS NULL;
            """;
        BindJob(command, request.Name, request.ProjectPath, request.Llm, request.EnvironmentId,
            request.Prompt, request.ExecutionMode, request.TimeoutMinutes, request.Enabled, executablePath, now);
        command.Parameters.AddWithValue("$id", id);
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        await ReplaceTriggersAsync(connection, transaction, id, request.Triggers, now, cancellationToken);
        if (!request.Enabled)
        {
            await using var cancel = connection.CreateCommand();
            cancel.Transaction = transaction;
            cancel.CommandText = """
                UPDATE JobRuns
                SET Status = $cancelled, EndedUTC = $now, ErrorMessage = 'Job disabled before the run started.'
                WHERE JobId = $id AND Status = $queued;
                """;
            cancel.Parameters.AddWithValue("$cancelled", (int)JobRunStatus.Cancelled);
            cancel.Parameters.AddWithValue("$queued", (int)JobRunStatus.Queued);
            cancel.Parameters.AddWithValue("$now", ToDb(now));
            cancel.Parameters.AddWithValue("$id", id);
            await cancel.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return await GetJobAsync(id, cancellationToken);
    }

    public async Task<bool> SoftDeleteJobAsync(long id, CancellationToken cancellationToken = default)
    {
        var now = ToDb(DateTime.UtcNow);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        int changed;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE Jobs SET Enabled = 0, DeletedUTC = $now, UpdatedUTC = $now
                WHERE Id = $id AND DeletedUTC IS NULL;
                """;
            command.Parameters.AddWithValue("$now", now);
            command.Parameters.AddWithValue("$id", id);
            changed = await command.ExecuteNonQueryAsync(cancellationToken);
        }
        if (changed > 0)
        {
            await using var cancel = connection.CreateCommand();
            cancel.Transaction = transaction;
            cancel.CommandText = """
                UPDATE JobRuns SET Status = $cancelled, EndedUTC = $now,
                    ErrorMessage = 'Job deleted before the run started.'
                WHERE JobId = $id AND Status = $queued;
                """;
            cancel.Parameters.AddWithValue("$now", now);
            cancel.Parameters.AddWithValue("$id", id);
            cancel.Parameters.AddWithValue("$cancelled", (int)JobRunStatus.Cancelled);
            cancel.Parameters.AddWithValue("$queued", (int)JobRunStatus.Queued);
            await cancel.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return changed > 0;
    }

    public async Task<int> CountJobsForEnvironmentAsync(int environmentId, CancellationToken cancellationToken = default)
    {
        // Count ALL non-deleted jobs referencing the environment, not just enabled ones. Jobs
        // default to disabled (Enabled DEFAULT 0), so an "enabled only" guard let the common case
        // (a freshly created, still-disabled job) silently have its EnvironmentId nulled by the
        // ON DELETE SET NULL foreign key when the environment was deleted.
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
                          AND NOT EXISTS (
                              SELECT 1 FROM Jobs j
                              WHERE j.EnvironmentId = e.Id AND j.DeletedUTC IS NULL
                          )
                    );
                    """;
                eligibility.Parameters.AddWithValue("$id", environmentId);
                var canDelete = Convert.ToInt32(
                    await eligibility.ExecuteScalarAsync(cancellationToken),
                    CultureInfo.InvariantCulture) != 0;
                if (!canDelete)
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                    return false;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();

            // BEGIN IMMEDIATE holds SQLite's writer reservation across this atomic rename. A stale
            // request whose row no longer exists returns above without ever touching the path, and
            // environment/job writers cannot race between the eligibility check and commit.
            stageFilesystemDeletion();

            await using (var delete = connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM Environments WHERE Id = $id;";
                delete.Parameters.AddWithValue("$id", environmentId);
                var changed = await delete.ExecuteNonQueryAsync(CancellationToken.None);
                if (changed != 1)
                {
                    throw new InvalidOperationException(
                        $"Environment {environmentId} changed during its guarded deletion.");
                }
            }

            // Once the filesystem is staged, finish the small DB critical section without request
            // cancellation so the caller receives a definite committed-or-rolled-back outcome.
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
                throw new EnvironmentDeleteRollbackException(
                    environmentId,
                    deleteException,
                    rollbackException);
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

    public Task<string?> EnqueueManualRunAsync(long jobId, string? contextJson = null, CancellationToken cancellationToken = default) =>
        EnqueueJobRunAsync(jobId, null, JobTriggerKind.Manual, $"manual:{Guid.NewGuid():N}", contextJson, requireEnabled: false, cancellationToken);

    public async Task<string?> EnqueueRetryAsync(
        string runId,
        string? contextJsonOverride = null,
        CancellationToken cancellationToken = default)
    {
        var retryId = Guid.NewGuid().ToString("N");
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO JobRuns
                (Id, JobId, TriggerId, TriggerKind, TriggerKey, Status, JobName, ProjectPath, Llm,
                 EnvironmentId, EnvironmentName, EnvironmentPath, EnvironmentArgs, Prompt,
                 ExecutionMode, TimeoutMinutes, ExecutablePath, TriggerContextJson, QueuedUTC)
            SELECT
                $retryId, source.JobId, NULL, $manual, $triggerKey, $queued,
                source.JobName, source.ProjectPath, source.Llm, source.EnvironmentId,
                source.EnvironmentName, source.EnvironmentPath, source.EnvironmentArgs,
                source.Prompt, source.ExecutionMode, source.TimeoutMinutes, source.ExecutablePath,
                CASE WHEN $contextJsonOverride IS NULL
                     THEN source.TriggerContextJson ELSE $contextJsonOverride END,
                $now
            FROM JobRuns source
            JOIN Jobs j ON j.Id = source.JobId
            WHERE source.Id = $runId AND j.DeletedUTC IS NULL
              AND source.Status IN ($succeeded, $failed, $cancelled, $timedOut, $interrupted);
            """;
        command.Parameters.AddWithValue("$retryId", retryId);
        command.Parameters.AddWithValue("$manual", (int)JobTriggerKind.Manual);
        command.Parameters.AddWithValue("$triggerKey", $"retry:{runId}:{retryId}");
        command.Parameters.AddWithValue("$queued", (int)JobRunStatus.Queued);
        command.Parameters.AddWithValue("$now", ToDb(DateTime.UtcNow));
        command.Parameters.AddWithValue("$runId", runId);
        command.Parameters.AddWithValue(
            "$contextJsonOverride",
            contextJsonOverride is null ? DBNull.Value : contextJsonOverride);
        command.Parameters.AddWithValue("$succeeded", (int)JobRunStatus.Succeeded);
        command.Parameters.AddWithValue("$failed", (int)JobRunStatus.Failed);
        command.Parameters.AddWithValue("$cancelled", (int)JobRunStatus.Cancelled);
        command.Parameters.AddWithValue("$timedOut", (int)JobRunStatus.TimedOut);
        command.Parameters.AddWithValue("$interrupted", (int)JobRunStatus.Interrupted);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0 ? retryId : null;
    }

    public async Task<IReadOnlyList<string>> EnqueueEventRunsAsync(
        string projectPath,
        JobTriggerKind kind,
        string eventKey,
        string? contextJson,
        CancellationToken cancellationToken = default)
    {
        if (kind is not (JobTriggerKind.Vca or JobTriggerKind.Commit))
            throw new ArgumentOutOfRangeException(nameof(kind));

        await using var connection = await OpenAsync(cancellationToken);
        var candidates = new List<(long JobId, long TriggerId)>();
        await using (var query = connection.CreateCommand())
        {
            query.CommandText = $"""
                SELECT j.Id, t.Id
                FROM Jobs j
                JOIN JobTriggers t ON t.JobId = j.Id AND t.Kind = $kind
                WHERE j.Enabled = 1 AND j.DeletedUTC IS NULL AND j.ProjectPath = $projectPath{ProjectPathCollation};
                """;
            query.Parameters.AddWithValue("$kind", (int)kind);
            query.Parameters.AddWithValue("$projectPath", NormalizeProjectPath(projectPath));
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                candidates.Add((reader.GetInt64(0), reader.GetInt64(1)));
        }

        var runIds = new List<string>(candidates.Count);
        foreach (var candidate in candidates)
        {
            var runId = await EnqueueJobRunAsync(
                candidate.JobId,
                candidate.TriggerId,
                kind,
                $"{kind.ToString().ToLowerInvariant()}:{candidate.JobId}:{eventKey}",
                contextJson,
                requireEnabled: true,
                cancellationToken);
            if (runId != null)
                runIds.Add(runId);
        }
        return runIds;
    }

    public async Task<int> EnqueueDueSchedulesAsync(DateTime nowUtc, CancellationToken cancellationToken = default)
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
                var scheduled = ParseDb(reader.GetString(2));
                due.Add((
                    reader.GetInt64(0),
                    reader.GetInt64(1),
                    scheduled,
                    new JobTriggerRequest(
                        JobTriggerKind.Schedule,
                        (JobScheduleKind)reader.GetInt32(3),
                        reader.IsDBNull(4) ? null : reader.GetInt32(4),
                        reader.IsDBNull(5) ? null : reader.GetString(5),
                        reader.GetInt32(6),
                        reader.IsDBNull(7) ? null : reader.GetString(7))));
            }
        }

        var queued = 0;
        foreach (var item in due)
        {
            DateTime next;
            try
            {
                next = JobScheduleCalculator.ComputeNext(item.Trigger, nowUtc);
            }
            catch (Exception ex)
            {
                // A schedule whose parameters no longer resolve (e.g. a time zone that was dropped
                // from the OS database) throws deterministically here on every poll. Because its
                // NextRunUTC is never advanced, the worker would re-select it forever and, before
                // this guard, crash the whole loop. Clear NextRunUTC so it stops being due; a later
                // job edit recomputes it. Transient DB failures below are NOT caught here — they
                // bubble up to the worker's per-iteration retry instead of disabling the trigger.
                Log.Error(ex, "[Jobs] Trigger {TriggerId} has an unresolvable schedule; clearing its next run", item.TriggerId);
                try
                {
                    await using var disable = connection.CreateCommand();
                    disable.CommandText = """
                        UPDATE JobTriggers SET LastRunUTC = NextRunUTC, NextRunUTC = NULL
                        WHERE Id = $id AND NextRunUTC = $expected;
                        """;
                    disable.Parameters.AddWithValue("$id", item.TriggerId);
                    disable.Parameters.AddWithValue("$expected", ToDb(item.ScheduledUtc));
                    await disable.ExecuteNonQueryAsync(cancellationToken);
                }
                catch (Exception disableEx)
                {
                    Log.Error(disableEx, "[Jobs] Could not clear failing trigger {TriggerId}", item.TriggerId);
                }
                continue;
            }

            await using var transaction = connection.BeginTransaction(deferred: false);
            await using var advance = connection.CreateCommand();
            advance.Transaction = transaction;
            advance.CommandText = """
                UPDATE JobTriggers SET LastRunUTC = NextRunUTC, NextRunUTC = $next
                WHERE Id = $id AND NextRunUTC = $expected;
                """;
            advance.Parameters.AddWithValue("$next", ToDb(next));
            advance.Parameters.AddWithValue("$id", item.TriggerId);
            advance.Parameters.AddWithValue("$expected", ToDb(item.ScheduledUtc));
            if (await advance.ExecuteNonQueryAsync(cancellationToken) == 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                continue;
            }

            var context = JsonSerializer.Serialize(
                new Dictionary<string, string>
                {
                    ["scheduledUtc"] = ToDb(item.ScheduledUtc),
                    ["coalescedAtUtc"] = ToDb(nowUtc)
                },
                AppJsonSerializerContext.Default.DictionaryStringString);
            var inserted = await InsertRunAsync(
                connection,
                transaction,
                item.JobId,
                item.TriggerId,
                JobTriggerKind.Schedule,
                $"schedule:{item.TriggerId}:{ToDb(item.ScheduledUtc)}",
                context,
                requireEnabled: true,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            if (inserted != null) queued++;
        }
        return queued;
    }

    public async Task<JobRunRecord?> ClaimNextRunAsync(
        string instanceId,
        int processId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        string? id;
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            // Two exclusions serialize concurrent runs:
            //  * the first NOT EXISTS keeps a single job from running twice at once;
            //  * the second stops a LiveWrite run from being claimed while another LiveWrite run
            //    (of any job) is already writing the same working tree. Review runs are read-only
            //    and IsolatedWrite runs each clone their own workspace, so only LiveWrite needs
            //    per-path serialization.
            select.CommandText = $"""
                SELECT r.Id
                FROM JobRuns r
                JOIN Jobs j ON j.Id = r.JobId
                WHERE r.Status = $queued
                  AND j.DeletedUTC IS NULL
                  AND NOT EXISTS (
                      SELECT 1 FROM JobRuns active
                      WHERE active.JobId = r.JobId AND active.Status = $running)
                  AND NOT (
                      r.ExecutionMode = $liveWrite
                      AND EXISTS (
                          SELECT 1 FROM JobRuns active
                          WHERE active.Status = $running
                            AND active.ExecutionMode = $liveWrite
                            AND active.ProjectPath = r.ProjectPath{ProjectPathCollation}))
                ORDER BY r.QueuedUTC, r.Id
                LIMIT 1;
                """;
            select.Parameters.AddWithValue("$queued", (int)JobRunStatus.Queued);
            select.Parameters.AddWithValue("$running", (int)JobRunStatus.Running);
            select.Parameters.AddWithValue("$liveWrite", (int)JobExecutionMode.LiveWrite);
            id = (string?)await select.ExecuteScalarAsync(cancellationToken);
        }
        if (id == null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        await using (var claim = connection.CreateCommand())
        {
            claim.Transaction = transaction;
            claim.CommandText = """
                UPDATE JobRuns SET Status = $running, StartedUTC = $now,
                    OwnerInstanceId = $instanceId, OwnerProcessId = $processId
                WHERE Id = $id AND Status = $queued;
                """;
            claim.Parameters.AddWithValue("$running", (int)JobRunStatus.Running);
            claim.Parameters.AddWithValue("$queued", (int)JobRunStatus.Queued);
            claim.Parameters.AddWithValue("$now", ToDb(DateTime.UtcNow));
            claim.Parameters.AddWithValue("$instanceId", instanceId);
            claim.Parameters.AddWithValue("$processId", processId);
            claim.Parameters.AddWithValue("$id", id);
            if (await claim.ExecuteNonQueryAsync(cancellationToken) == 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return null;
            }
        }
        await transaction.CommitAsync(cancellationToken);
        return await GetRunAsync(id, cancellationToken);
    }

    public async Task<IReadOnlyList<JobRunRecord>> GetRunsAsync(
        long? jobId = null,
        int limit = 100,
        CancellationToken cancellationToken = default)
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

    public async Task<JobSnapshotArtifactReferences> GetActiveSnapshotArtifactReferencesAsync(
        CancellationToken cancellationToken = default)
    {
        var activeRunIds = new HashSet<string>(StringComparer.Ordinal);
        var activeSnapshotIds = new HashSet<string>(StringComparer.Ordinal);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, TriggerContextJson
            FROM JobRuns
            WHERE Status IN ($queued, $running);
            """;
        command.Parameters.AddWithValue("$queued", (int)JobRunStatus.Queued);
        command.Parameters.AddWithValue("$running", (int)JobRunStatus.Running);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            activeRunIds.Add(reader.GetString(0));
            if (reader.IsDBNull(1))
                continue;

            try
            {
                using var context = JsonDocument.Parse(reader.GetString(1));
                if (context.RootElement.ValueKind != JsonValueKind.Object
                    || !context.RootElement.TryGetProperty(
                        JobWorkspaceService.StagedSnapshotIdContextKey,
                        out var snapshotProperty)
                    || snapshotProperty.ValueKind != JsonValueKind.String
                    || !Guid.TryParseExact(snapshotProperty.GetString(), "N", out var snapshotId))
                {
                    continue;
                }
                activeSnapshotIds.Add(snapshotId.ToString("N"));
            }
            catch (JsonException)
            {
                // Malformed context fails closed during workspace preparation. It cannot name a
                // trustworthy shared artifact, but its run id still protects any per-run copy.
            }
        }

        return new JobSnapshotArtifactReferences(activeRunIds, activeSnapshotIds);
    }

    public async Task AppendRunLogAsync(
        string runId,
        long sequence,
        string stream,
        string content,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(content)) return;
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO JobRunLogs (RunId, Sequence, TimestampUTC, Stream, Content)
            VALUES ($runId, $sequence, $now, $stream, $content);
            """;
        command.Parameters.AddWithValue("$runId", runId);
        command.Parameters.AddWithValue("$sequence", sequence);
        command.Parameters.AddWithValue("$now", ToDb(DateTime.UtcNow));
        command.Parameters.AddWithValue("$stream", stream);
        command.Parameters.AddWithValue("$content", content);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<JobRunLogResponse>> GetRunLogsAsync(
        string runId,
        long afterSequence,
        int limit = 1000,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var logs = new List<JobRunLogResponse>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Sequence, TimestampUTC, Stream, Content
            FROM JobRunLogs WHERE RunId = $runId AND Sequence > $after
            ORDER BY Sequence LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$runId", runId);
        command.Parameters.AddWithValue("$after", Math.Max(0, afterSequence));
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 5000));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            logs.Add(new JobRunLogResponse(reader.GetInt64(0), ParseDb(reader.GetString(1)), reader.GetString(2), reader.GetString(3)));
        return logs;
    }

    public async Task CompleteRunAsync(
        string runId,
        JobRunStatus status,
        int? exitCode,
        string? resultText,
        string? errorMessage,
        string? expectedOwnerInstanceId = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        // Only finalize a run that is still Running and (when an owner is supplied) still owned by
        // the caller. This makes completion a no-op for a stale executor whose lease was taken over:
        // the replacement worker has already flipped the run to Interrupted, so this UPDATE matches
        // zero rows and cannot overwrite the correct terminal status.
        command.CommandText = """
            UPDATE JobRuns SET Status = $status, EndedUTC = $ended, ExitCode = $exitCode,
                ResultText = $resultText, ErrorMessage = $errorMessage
            WHERE Id = $id AND Status = $running
              AND ($expectedOwner IS NULL OR OwnerInstanceId = $expectedOwner);
            """;
        command.Parameters.AddWithValue("$status", (int)status);
        command.Parameters.AddWithValue("$running", (int)JobRunStatus.Running);
        command.Parameters.AddWithValue("$ended", ToDb(DateTime.UtcNow));
        command.Parameters.AddWithValue("$exitCode", exitCode is null ? DBNull.Value : exitCode.Value);
        command.Parameters.AddWithValue("$resultText", resultText is null ? DBNull.Value : resultText);
        command.Parameters.AddWithValue("$errorMessage", errorMessage is null ? DBNull.Value : errorMessage);
        command.Parameters.AddWithValue("$expectedOwner", expectedOwnerInstanceId is null ? DBNull.Value : expectedOwnerInstanceId);
        command.Parameters.AddWithValue("$id", runId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SetRunWorkspaceAsync(string runId, string? workspacePath, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE JobRuns SET WorkspacePath = $path WHERE Id = $id;";
        command.Parameters.AddWithValue("$path", workspacePath is null ? DBNull.Value : workspacePath);
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

    public async Task<int> MarkRunningRunsInterruptedAsync(string reason, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE JobRuns SET Status = $interrupted, EndedUTC = $now, ErrorMessage = $reason
            WHERE Status = $running;
            """;
        command.Parameters.AddWithValue("$interrupted", (int)JobRunStatus.Interrupted);
        command.Parameters.AddWithValue("$running", (int)JobRunStatus.Running);
        command.Parameters.AddWithValue("$now", ToDb(DateTime.UtcNow));
        command.Parameters.AddWithValue("$reason", reason);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> TryAcquireWorkerLeaseAsync(
        JobWorkerLeaseRecord lease,
        DateTime staleBeforeUtc,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO JobWorkerLease (SingletonId, InstanceId, ProcessId, Version, StartedUTC, HeartbeatUTC)
            VALUES (1, $instanceId, $processId, $version, $started, $heartbeat)
            ON CONFLICT(SingletonId) DO UPDATE SET
                InstanceId = excluded.InstanceId,
                ProcessId = excluded.ProcessId,
                Version = excluded.Version,
                StartedUTC = excluded.StartedUTC,
                HeartbeatUTC = excluded.HeartbeatUTC
            WHERE JobWorkerLease.InstanceId = excluded.InstanceId
               OR JobWorkerLease.HeartbeatUTC < $staleBefore;
            """;
        BindLease(command, lease);
        command.Parameters.AddWithValue("$staleBefore", ToDb(staleBeforeUtc));
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task UpsertWorkerLeaseAsync(JobWorkerLeaseRecord lease, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO JobWorkerLease (SingletonId, InstanceId, ProcessId, Version, StartedUTC, HeartbeatUTC)
            VALUES (1, $instanceId, $processId, $version, $started, $heartbeat)
            ON CONFLICT(SingletonId) DO UPDATE SET
                ProcessId = excluded.ProcessId, Version = excluded.Version,
                HeartbeatUTC = excluded.HeartbeatUTC
            WHERE JobWorkerLease.InstanceId = excluded.InstanceId;
            """;
        BindLease(command, lease);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<JobWorkerLeaseRecord?> GetWorkerLeaseAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT InstanceId, ProcessId, Version, StartedUTC, HeartbeatUTC FROM JobWorkerLease WHERE SingletonId = 1;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new JobWorkerLeaseRecord(reader.GetString(0), reader.GetInt32(1), reader.GetString(2), ParseDb(reader.GetString(3)), ParseDb(reader.GetString(4)))
            : null;
    }

    public async Task DeleteWorkerLeaseAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM JobWorkerLease WHERE SingletonId = 1 AND InstanceId = $instanceId;";
        command.Parameters.AddWithValue("$instanceId", instanceId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<(int Definitions, int Runs)> GetLegacyCronCountsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var exists = connection.CreateCommand();
        exists.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='CronJobRuns';";
        if (Convert.ToInt32(await exists.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 0)
            return (0, 0);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT
                    (SELECT COUNT(*) FROM Environments WHERE IsCronJob = 1),
                    (SELECT COUNT(*) FROM CronJobRuns);
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken) ? (reader.GetInt32(0), reader.GetInt32(1)) : (0, 0);
        }
        catch (SqliteException)
        {
            return (0, 0);
        }
    }

    private async Task<string?> EnqueueJobRunAsync(
        long jobId,
        long? triggerId,
        JobTriggerKind kind,
        string triggerKey,
        string? contextJson,
        bool requireEnabled,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var runId = await InsertRunAsync(connection, transaction, jobId, triggerId, kind, triggerKey, contextJson, requireEnabled, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return runId;
    }

    private static async Task<string?> InsertRunAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long jobId,
        long? triggerId,
        JobTriggerKind kind,
        string triggerKey,
        string? contextJson,
        bool requireEnabled,
        CancellationToken cancellationToken)
    {
        var runId = Guid.NewGuid().ToString("N");
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO JobRuns
                (Id, JobId, TriggerId, TriggerKind, TriggerKey, Status, JobName, ProjectPath, Llm,
                 EnvironmentId, EnvironmentName, EnvironmentPath, EnvironmentArgs, Prompt,
                 ExecutionMode, TimeoutMinutes, ExecutablePath, TriggerContextJson, QueuedUTC)
            SELECT
                $runId, j.Id, $triggerId, $triggerKind, $triggerKey, $status, j.Name, j.ProjectPath, j.Llm,
                j.EnvironmentId, e.CustomName, e.Path, COALESCE(e.CustomArgs, ''), j.Prompt,
                j.ExecutionMode, j.TimeoutMinutes, j.ExecutablePath, $contextJson, $queuedUtc
            FROM Jobs j
            LEFT JOIN Environments e ON e.Id = j.EnvironmentId
            WHERE j.Id = $jobId AND ($requireEnabled = 0 OR j.Enabled = 1) AND j.DeletedUTC IS NULL;
            """;
        command.Parameters.AddWithValue("$runId", runId);
        command.Parameters.AddWithValue("$jobId", jobId);
        command.Parameters.AddWithValue("$triggerId", triggerId is null ? DBNull.Value : triggerId.Value);
        command.Parameters.AddWithValue("$triggerKind", (int)kind);
        command.Parameters.AddWithValue("$triggerKey", triggerKey);
        command.Parameters.AddWithValue("$status", (int)JobRunStatus.Queued);
        command.Parameters.AddWithValue("$contextJson", contextJson is null ? DBNull.Value : contextJson);
        command.Parameters.AddWithValue("$queuedUtc", ToDb(DateTime.UtcNow));
        command.Parameters.AddWithValue("$requireEnabled", requireEnabled ? 1 : 0);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0 ? runId : null;
    }

    private static void BindLease(SqliteCommand command, JobWorkerLeaseRecord lease)
    {
        command.Parameters.AddWithValue("$instanceId", lease.InstanceId);
        command.Parameters.AddWithValue("$processId", lease.ProcessId);
        command.Parameters.AddWithValue("$version", lease.Version);
        command.Parameters.AddWithValue("$started", ToDb(lease.StartedUtc));
        command.Parameters.AddWithValue("$heartbeat", ToDb(lease.HeartbeatUtc));
    }

    private static async Task ReplaceTriggersAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long jobId,
        IReadOnlyList<JobTriggerRequest> triggers,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        // Snapshot the current triggers first so editing an unrelated field (name, prompt) does not
        // silently re-arm an unchanged schedule to "now". UNIQUE(JobId, Kind) guarantees at most one
        // trigger per kind, so a same-Kind prior row is the match.
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
                // Preserve the existing cadence only when every schedule parameter is unchanged;
                // any real edit recomputes the next run from now.
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

            // LastRunUTC is historical fact, not a cadence input — carry it forward for the same kind.
            var lastRun = prior?.LastRunUtc;

            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO JobTriggers
                    (JobId, Kind, ScheduleKind, IntervalMinutes, LocalTime, DaysOfWeekMask,
                     TimeZoneId, NextRunUTC, LastRunUTC)
                VALUES
                    ($jobId, $kind, $scheduleKind, $intervalMinutes, $localTime, $daysMask,
                     $timeZone, $nextRun, $lastRun);
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

    private static void BindJob(
        SqliteCommand command,
        string name,
        string projectPath,
        LLM llm,
        int? environmentId,
        string prompt,
        JobExecutionMode executionMode,
        int timeoutMinutes,
        bool enabled,
        string? executablePath,
        DateTime now)
    {
        command.Parameters.AddWithValue("$name", name.Trim());
        command.Parameters.AddWithValue("$projectPath", NormalizeProjectPath(projectPath));
        command.Parameters.AddWithValue("$llm", (int)llm);
        command.Parameters.AddWithValue("$environmentId", environmentId is null ? DBNull.Value : environmentId.Value);
        command.Parameters.AddWithValue("$prompt", prompt.Trim());
        command.Parameters.AddWithValue("$executionMode", (int)executionMode);
        command.Parameters.AddWithValue("$timeoutMinutes", timeoutMinutes);
        command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
        command.Parameters.AddWithValue("$executablePath", executablePath is null ? DBNull.Value : executablePath);
        command.Parameters.AddWithValue("$now", ToDb(now));
    }

    private static JobDefinitionRecord ReadJob(SqliteDataReader reader)
    {
        return new JobDefinitionRecord(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetString(2),
            (LLM)reader.GetInt32(3),
            reader.IsDBNull(4) ? null : reader.GetInt32(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? "" : reader.GetString(7),
            reader.GetString(8),
            (JobExecutionMode)reader.GetInt32(9),
            reader.GetInt32(10),
            reader.GetInt32(11) != 0,
            reader.IsDBNull(12) ? null : reader.GetString(12),
            ParseDb(reader.GetString(13)),
            ParseDb(reader.GetString(14)),
            reader.IsDBNull(15) ? null : ParseDb(reader.GetString(15)),
            []);
    }

    private static async Task<IReadOnlyList<JobTriggerDto>> ReadTriggersAsync(
        SqliteConnection connection,
        long jobId,
        CancellationToken cancellationToken)
    {
        var triggers = new List<JobTriggerDto>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Kind, ScheduleKind, IntervalMinutes, LocalTime, DaysOfWeekMask,
                   TimeZoneId, NextRunUTC, LastRunUTC
            FROM JobTriggers WHERE JobId = $jobId ORDER BY Kind, Id;
            """;
        command.Parameters.AddWithValue("$jobId", jobId);
        await using var triggerReader = await command.ExecuteReaderAsync(cancellationToken);
        while (await triggerReader.ReadAsync(cancellationToken))
            triggers.Add(ReadTrigger(triggerReader));
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
            SELECT t.JobId, t.Id, t.Kind, t.ScheduleKind, t.IntervalMinutes, t.LocalTime,
                   t.DaysOfWeekMask, t.TimeZoneId, t.NextRunUTC, t.LastRunUTC
            FROM JobTriggers t
            JOIN Jobs j ON j.Id = t.JobId
            WHERE ($includeDeleted = 1 OR j.DeletedUTC IS NULL)
              AND ($projectPath IS NULL OR j.ProjectPath = $projectPath{ProjectPathCollation})
            ORDER BY t.JobId, t.Kind, t.Id;
            """;
        command.Parameters.AddWithValue("$includeDeleted", includeDeleted ? 1 : 0);
        command.Parameters.AddWithValue(
            "$projectPath",
            projectPath is null ? DBNull.Value : NormalizeProjectPath(projectPath));

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
        reader.GetString(0), reader.GetInt64(1), reader.IsDBNull(2) ? null : reader.GetInt64(2),
        (JobTriggerKind)reader.GetInt32(3), reader.GetString(4), (JobRunStatus)reader.GetInt32(5),
        reader.GetString(6), reader.GetString(7), (LLM)reader.GetInt32(8),
        reader.IsDBNull(9) ? null : reader.GetInt32(9), reader.IsDBNull(10) ? null : reader.GetString(10),
        reader.IsDBNull(11) ? null : reader.GetString(11), reader.IsDBNull(12) ? "" : reader.GetString(12),
        reader.GetString(13), (JobExecutionMode)reader.GetInt32(14), reader.GetInt32(15),
        reader.IsDBNull(16) ? null : reader.GetString(16), reader.IsDBNull(17) ? null : reader.GetString(17),
        ParseDb(reader.GetString(18)), reader.IsDBNull(19) ? null : ParseDb(reader.GetString(19)),
        reader.IsDBNull(20) ? null : ParseDb(reader.GetString(20)), reader.IsDBNull(21) ? null : reader.GetInt32(21),
        reader.IsDBNull(22) ? null : reader.GetString(22), reader.IsDBNull(23) ? null : reader.GetString(23),
        reader.IsDBNull(24) ? null : reader.GetString(24), reader.GetInt32(25) != 0,
        reader.IsDBNull(26) ? null : reader.GetString(26), reader.IsDBNull(27) ? null : reader.GetInt32(27));

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
        using var command = connection.CreateCommand();
        command.CommandText = SchemaSql;
        command.ExecuteNonQuery();
    }

    // Windows filesystems compare paths case-insensitively; Linux does not. Project paths are stored
    // with their ORIGINAL casing (so the Jobs UI and the child process working directory read
    // naturally instead of SHOUTING) — case-insensitive matching on Windows is applied at the SQL
    // comparison sites through this collation suffix rather than by uppercasing the stored value.
    private static readonly string ProjectPathCollation =
        OperatingSystem.IsWindows() ? " COLLATE NOCASE" : string.Empty;

    public static string NormalizeProjectPath(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));

    private static string ToDb(DateTime value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTime ParseDb(string value) => DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();

    private const string JobSelectSql = """
        SELECT j.Id, j.Name, j.ProjectPath, j.Llm, j.EnvironmentId,
               e.CustomName, e.Path, e.CustomArgs, j.Prompt, j.ExecutionMode,
               j.TimeoutMinutes, j.Enabled, j.ExecutablePath, j.CreatedUTC,
               j.UpdatedUTC, j.DeletedUTC
        FROM Jobs j LEFT JOIN Environments e ON e.Id = j.EnvironmentId
        """;

    private const string RunSelectSql = """
        SELECT r.Id, r.JobId, r.TriggerId, r.TriggerKind, r.TriggerKey, r.Status,
               r.JobName, r.ProjectPath, r.Llm, r.EnvironmentId, r.EnvironmentName,
               r.EnvironmentPath, r.EnvironmentArgs, r.Prompt, r.ExecutionMode,
               r.TimeoutMinutes, r.ExecutablePath, r.TriggerContextJson, r.QueuedUTC,
               r.StartedUTC, r.EndedUTC, r.ExitCode, r.ResultText, r.ErrorMessage,
               r.WorkspacePath, r.CancelRequested, r.OwnerInstanceId, r.OwnerProcessId
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
            Llm INTEGER NOT NULL,
            EnvironmentId INTEGER,
            Prompt TEXT NOT NULL,
            ExecutionMode INTEGER NOT NULL DEFAULT 0,
            TimeoutMinutes INTEGER NOT NULL DEFAULT 30,
            Enabled INTEGER NOT NULL DEFAULT 0,
            ExecutablePath TEXT,
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
            TriggerId INTEGER,
            TriggerKind INTEGER NOT NULL,
            TriggerKey TEXT NOT NULL UNIQUE,
            Status INTEGER NOT NULL,
            JobName TEXT NOT NULL,
            ProjectPath TEXT NOT NULL,
            Llm INTEGER NOT NULL,
            EnvironmentId INTEGER,
            EnvironmentName TEXT,
            EnvironmentPath TEXT,
            EnvironmentArgs TEXT NOT NULL DEFAULT '',
            Prompt TEXT NOT NULL,
            ExecutionMode INTEGER NOT NULL,
            TimeoutMinutes INTEGER NOT NULL,
            ExecutablePath TEXT,
            TriggerContextJson TEXT,
            QueuedUTC TEXT NOT NULL,
            StartedUTC TEXT,
            EndedUTC TEXT,
            ExitCode INTEGER,
            ResultText TEXT,
            ErrorMessage TEXT,
            WorkspacePath TEXT,
            CancelRequested INTEGER NOT NULL DEFAULT 0,
            OwnerInstanceId TEXT,
            OwnerProcessId INTEGER,
            FOREIGN KEY (JobId) REFERENCES Jobs(Id),
            FOREIGN KEY (TriggerId) REFERENCES JobTriggers(Id) ON DELETE SET NULL
        );

        CREATE TABLE IF NOT EXISTS JobRunLogs (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            RunId TEXT NOT NULL,
            Sequence INTEGER NOT NULL,
            TimestampUTC TEXT NOT NULL,
            Stream TEXT NOT NULL,
            Content TEXT NOT NULL,
            FOREIGN KEY (RunId) REFERENCES JobRuns(Id),
            UNIQUE(RunId, Sequence)
        );

        CREATE TABLE IF NOT EXISTS JobWorkerLease (
            SingletonId INTEGER PRIMARY KEY CHECK (SingletonId = 1),
            InstanceId TEXT NOT NULL,
            ProcessId INTEGER NOT NULL,
            Version TEXT NOT NULL,
            StartedUTC TEXT NOT NULL,
            HeartbeatUTC TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS idx_jobs_project ON Jobs(ProjectPath, Enabled, DeletedUTC);
        CREATE INDEX IF NOT EXISTS idx_job_triggers_due ON JobTriggers(Kind, NextRunUTC);
        CREATE INDEX IF NOT EXISTS idx_job_runs_queue ON JobRuns(Status, QueuedUTC);
        CREATE INDEX IF NOT EXISTS idx_job_runs_job ON JobRuns(JobId, QueuedUTC DESC);
        CREATE INDEX IF NOT EXISTS idx_job_logs_run ON JobRunLogs(RunId, Sequence);
        """;
}

public sealed class EnvironmentDeleteRollbackException : Exception
{
    internal EnvironmentDeleteRollbackException(
        int environmentId,
        Exception deleteException,
        Exception rollbackException)
        : base(
            $"Could not confirm rollback while deleting environment {environmentId}.",
            new AggregateException(deleteException, rollbackException))
    {
    }
}
