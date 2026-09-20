using Microsoft.Data.Sqlite;
using VibeRails.DTOs;

namespace VibeRails.DB;

public sealed partial class JobStore
{
    private static async Task<List<string>> EnqueueDueBoardRunsAsync(SqliteConnection connection, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var runIds = await EnqueueDueBoardRunsAsync(connection, nowUtc, additional: false, cancellationToken);
        runIds.AddRange(await EnqueueDueBoardRunsAsync(connection, nowUtc, additional: true, cancellationToken));
        return runIds;
    }

    private static async Task<List<string>> EnqueueDueBoardRunsAsync(SqliteConnection connection, DateTime nowUtc, bool additional, CancellationToken cancellationToken)
    {
        // Identifiers are internal constants, never caller input. Supporting both queues keeps
        // previous schedulers compatible and also works before board/7 has been initialized.
        var pendingTable = additional ? "BoardPendingAdditionalAutomations" : "BoardPendingAutomations";
        var settingsTable = additional ? "BoardLaneAdditionalAutomations" : "BoardLaneAutomations";
        var runIds = new List<string>();
        await using (var exists = connection.CreateCommand())
        {
            // Jobs can be initialized in a standalone host before the Board schema exists.
            exists.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'table' AND name = $table;";
            exists.Parameters.AddWithValue("$table", pendingTable);
            if (Convert.ToInt64(await exists.ExecuteScalarAsync(cancellationToken)) == 0) return runIds;
        }
        var now = new DateTimeOffset(nowUtc).ToUnixTimeMilliseconds();
        await using (var due = connection.CreateCommand())
        {
            due.CommandText = $"SELECT EXISTS(SELECT 1 FROM {pendingTable} WHERE DueUnixMs <= $now);";
            due.Parameters.AddWithValue("$now", now);
            if (Convert.ToInt64(await due.ExecuteScalarAsync(cancellationToken)) == 0) return runIds;
        }

        // Consumption and run snapshots commit together. Independent roots cannot double queue;
        // a concurrent move either cancels the pending transition first or follows this enqueue.
        await using var transaction = connection.BeginTransaction(deferred: false);
        var events = new List<(string CardId, long JobId, string Key, bool Valid)>();
        await using (var query = connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = $"""
                SELECT p.CardId, p.JobId, 'board-lane:VB-' || c.Number || ':' || p.ColumnId || ':' || p.EventKey,
                    c.ColumnId = p.ColumnId AND a.JobId = p.JobId
                    AND j.ProjectPath = c.ProjectPath{ProjectPathCollation} AND j.Enabled = 1 AND j.DeletedUTC IS NULL
                FROM {pendingTable} p
                JOIN BoardCards c ON c.Id = p.CardId
                LEFT JOIN {settingsTable} a ON a.ColumnId = p.ColumnId AND a.JobId = p.JobId
                LEFT JOIN Jobs j ON j.Id = p.JobId
                WHERE p.DueUnixMs <= $now ORDER BY p.DueUnixMs, p.CardId, p.JobId LIMIT 100;
                """;
            query.Parameters.AddWithValue("$now", now);
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                events.Add((reader.GetString(0), reader.GetInt64(1), reader.GetString(2), !reader.IsDBNull(3) && reader.GetBoolean(3)));
        }
        foreach (var item in events)
        {
            if (item.Valid)
            {
                var runId = await InsertRunAsync(connection, transaction, item.JobId, JobTriggerKind.BoardLane, item.Key, requireEnabled: true, cancellationToken);
                if (runId is not null) runIds.Add(runId);
            }
            // Disabled/deleted jobs and active-job overlap are skipped, just like schedule events.
            // They must not spring to life later when a job is enabled or a long run completes.
            await using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = $"DELETE FROM {pendingTable} WHERE CardId = $card AND JobId = $job;";
            delete.Parameters.AddWithValue("$card", item.CardId);
            delete.Parameters.AddWithValue("$job", item.JobId);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return runIds;
    }
}
