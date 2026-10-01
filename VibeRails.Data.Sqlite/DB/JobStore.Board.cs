using Microsoft.Data.Sqlite;
using Serilog;
using VibeRails.DTOs;
using VibeRails.Services.Board;

namespace VibeRails.DB;

public sealed partial class JobStore
{
    private async Task<List<string>> EnqueueDueBoardRunsAsync(SqliteConnection connection,
        DateTime nowUtc, CancellationToken cancellationToken)
    {
        var runIds = new List<string>();
        if (_boards is null) return runIds;

        // board.db is a separate file that a stdio MCP host can hold, a newer build can stamp, or
        // a user can damage. None of that may gate schedules, commit triggers or the launches that
        // follow this drain in the same cycle, so a failed read is logged and retried next tick.
        IReadOnlyList<BoardLaneAutomationEvent> due;
        try
        {
            due = await _boards.GetDueLaneAutomationsAsync(nowUtc, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            Log.Warning(ex, "[Jobs] Board lane Automations were not read this cycle; schedules and launches continue.");
            return runIds;
        }

        // Keep Board access behind its contract so a future remote Board can use this handoff.
        // Commit the local run/action snapshot before acknowledging the durable Board event.
        // If acknowledgement fails, the same TriggerKey makes the next attempt harmless.
        // There is deliberately no transaction or writer lock spanning the two databases.
        foreach (var entry in due)
        {
            try
            {
                var current = entry.IsCurrent && await _boards.IsLaneAutomationCurrentAsync(entry, cancellationToken);
                var reviewLaunch = current ? await PrepareReviewAsync(entry.JobId, JobBoardContext.GetCardKey(JobTriggerKind.BoardLane, entry.TriggerKey), cancellationToken) : null;
                BoardLaneAutomationDispatch dispatch;
                await using (var transaction = connection.BeginTransaction(deferred: false))
                {
                    var runId = current ? await InsertRunAsync(connection, transaction, entry.JobId,
                        JobTriggerKind.BoardLane, entry.TriggerKey, requireEnabled: true, cancellationToken,
                        expectedProjectPath: entry.ProjectPath, reviewLaunch: reviewLaunch) : null;
                    if (runId is not null) runIds.Add(runId);
                    dispatch = runId is not null ? new("Queued", "Lane Automation queued.", runId)
                        : await DescribeBoardRunRejectionAsync(connection, transaction, entry, current, cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                }

                await _boards.RecordLaneAutomationDispatchAsync(entry, dispatch, nowUtc, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // One locked or poison entry must not starve the entries after it; the next cycle
                // re-reads it. A run committed above stays committed and its TriggerKey dedups.
                Log.Warning(ex, "[Jobs] Board lane Automation {TriggerKey} was not processed this cycle.", entry.TriggerKey);
            }
        }
        return runIds;
    }

    private static async Task<BoardLaneAutomationDispatch> DescribeBoardRunRejectionAsync(SqliteConnection connection,
        SqliteTransaction transaction, BoardLaneAutomationEvent entry, bool current, CancellationToken cancellationToken)
    {
        // Inspect under the same state.db writer transaction as InsertRunAsync. Dedup takes
        // precedence over current eligibility: a crash after commit must acknowledge that run.
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT Id FROM JobRuns WHERE JobId = $job AND TriggerKind = $kind AND TriggerKey = $key
                AND ProjectPath = $project{ProjectPathCollation} LIMIT 1;
            """;
        command.Parameters.AddWithValue("$job", entry.JobId);
        command.Parameters.AddWithValue("$kind", (int)JobTriggerKind.BoardLane);
        command.Parameters.AddWithValue("$key", entry.TriggerKey);
        command.Parameters.AddWithValue("$project", entry.ProjectPath);
        if (await command.ExecuteScalarAsync(cancellationToken) is string existing)
            return new("Queued", "Already enqueued; recovered the committed run.", existing);
        if (!current) return new("Cancelled", "Lane entry is no longer current.");
        command.CommandText = $"""
            SELECT CASE
                WHEN DeletedUTC IS NOT NULL THEN 'Automation was deleted.'
                WHEN ProjectPath <> $project{ProjectPathCollation} THEN 'Automation belongs to another project.'
                WHEN Enabled = 0 THEN 'Automation is disabled.'
                WHEN NOT EXISTS (SELECT 1 FROM JobActions WHERE JobId = $job) THEN 'Automation has no actions.'
                ELSE NULL END FROM Jobs WHERE Id = $job;
            """;
        var reason = await command.ExecuteScalarAsync(cancellationToken);
        if (reason is null) return new("Skipped", "Automation no longer exists.");
        if (reason is string message) return new("Skipped", message);
        command.CommandText = "SELECT Id FROM JobRuns WHERE JobId = $job AND Status IN ($queued, $running) LIMIT 1;";
        command.Parameters.AddWithValue("$queued", (int)JobRunStatus.Queued);
        command.Parameters.AddWithValue("$running", (int)JobRunStatus.Running);
        if (await command.ExecuteScalarAsync(cancellationToken) is string active)
            return new("Waiting", $"Automation is busy with run {active}; waiting for its turn.");
        return new("Waiting", "Run was not inserted; the scheduler will retry.");
    }
}
