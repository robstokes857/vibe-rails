using Microsoft.Data.Sqlite;
using VibeRails.DTOs;

namespace VibeRails.DB;

public sealed partial class JobStore
{
    private async Task<List<string>> EnqueueDueBoardRunsAsync(SqliteConnection connection,
        DateTime nowUtc, CancellationToken cancellationToken)
    {
        var runIds = new List<string>();
        if (_boards is null) return runIds;

        // Keep Board access behind its contract so a future remote Board can use this handoff.
        // Commit the local run/action snapshot before acknowledging the durable Board event.
        // If acknowledgement fails, the same TriggerKey makes the next attempt harmless.
        // There is deliberately no transaction or writer lock spanning the two databases.
        foreach (var entry in await _boards.GetDueLaneAutomationsAsync(nowUtc, cancellationToken))
        {
            if (entry.IsCurrent)
            {
                await using var transaction = connection.BeginTransaction(deferred: false);
                await using var validate = connection.CreateCommand();
                validate.Transaction = transaction;
                validate.CommandText = $"""
                    SELECT 1 FROM Jobs WHERE Id = $job AND ProjectPath = $project{ProjectPathCollation}
                        AND Enabled = 1 AND DeletedUTC IS NULL;
                    """;
                validate.Parameters.AddWithValue("$job", entry.JobId);
                validate.Parameters.AddWithValue("$project", entry.ProjectPath);
                if (await validate.ExecuteScalarAsync(cancellationToken) is not null)
                {
                    var runId = await InsertRunAsync(connection, transaction, entry.JobId,
                        JobTriggerKind.BoardLane, entry.TriggerKey, requireEnabled: true, cancellationToken);
                    if (runId is not null) runIds.Add(runId);
                }
                await transaction.CommitAsync(cancellationToken);
            }

            // Disabled/deleted jobs and overlap are consumed just as schedule events are.
            // Exact event identity prevents this acknowledgement deleting a later lane entry.
            await _boards.AcknowledgeLaneAutomationAsync(entry, cancellationToken);
        }
        return runIds;
    }
}
