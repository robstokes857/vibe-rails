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
                if (entry.IsCurrent)
                {
                    await using var transaction = connection.BeginTransaction(deferred: false);
                    // The project match is the only Board-specific gate; enabled/deleted/overlap
                    // live in InsertRunAsync so every trigger path shares one definition.
                    var runId = await InsertRunAsync(connection, transaction, entry.JobId,
                        JobTriggerKind.BoardLane, entry.TriggerKey, requireEnabled: true, cancellationToken,
                        expectedProjectPath: entry.ProjectPath);
                    if (runId is not null) runIds.Add(runId);
                    await transaction.CommitAsync(cancellationToken);
                }

                // Disabled/deleted jobs and overlap are consumed just as schedule events are.
                // Exact event identity prevents this acknowledgement deleting a later lane entry.
                await _boards.AcknowledgeLaneAutomationAsync(entry, cancellationToken);
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
}
