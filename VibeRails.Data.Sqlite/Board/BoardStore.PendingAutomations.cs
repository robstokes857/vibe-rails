using Microsoft.Data.Sqlite;

namespace VibeRails.Services.Board;

public sealed partial class BoardStore
{
    private const string PendingLaneEntriesSql = """
        SELECT CardId, ColumnId, JobId, EventKey, DueUnixMs FROM BoardPendingAutomations
        UNION ALL
        SELECT CardId, ColumnId, JobId, EventKey, DueUnixMs FROM BoardPendingAdditionalAutomations
        """;

    public async Task<IReadOnlyList<BoardLaneAutomationEvent>> GetDueLaneAutomationsAsync(
        DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var query = connection.CreateCommand();
        // One oldest entry per Job prevents a backlog for one busy Automation from hiding
        // other Jobs beyond the batch limit. Rotate attempted Jobs durably across roots/restarts.
        query.CommandText = $"""
            WITH pending AS ({PendingLaneEntriesSql}), ranked AS (
                SELECT *, ROW_NUMBER() OVER (PARTITION BY JobId ORDER BY DueUnixMs, CardId, EventKey) AS rank
                FROM pending WHERE DueUnixMs <= $now
            )
            SELECT p.CardId, p.JobId, p.EventKey, c.ProjectPath,
                'board-lane:' || {CardKeySql} || ':' || p.ColumnId || ':' || p.EventKey,
                c.ColumnId = p.ColumnId AND c.DeletedUTC IS NULL AND (
                    EXISTS (SELECT 1 FROM BoardLaneAutomations a WHERE a.ColumnId = p.ColumnId AND a.JobId = p.JobId)
                    OR EXISTS (SELECT 1 FROM BoardLaneAdditionalAutomations a WHERE a.ColumnId = p.ColumnId AND a.JobId = p.JobId))
            FROM ranked p JOIN BoardCards c ON c.Id = p.CardId
            {CardPrefixJoinSql}
            LEFT JOIN BoardLaneAutomationDispatch d ON d.EventKey = p.EventKey AND d.JobId = p.JobId
            WHERE p.rank = 1 AND NOT EXISTS (
                SELECT 1 FROM BoardLaneWorkflowSteps target
                JOIN BoardLaneWorkflowSteps prior ON prior.WorkflowId = target.WorkflowId AND prior.Position < target.Position
                JOIN pending earlier ON earlier.EventKey = prior.EventKey AND earlier.JobId = prior.JobId
                WHERE target.EventKey = p.EventKey AND target.JobId = p.JobId)
            ORDER BY COALESCE(d.LastAttemptUnixMs, 0), p.DueUnixMs, p.CardId, p.JobId LIMIT 100;
            """;
        query.Parameters.AddWithValue("$now", new DateTimeOffset(nowUtc).ToUnixTimeMilliseconds());
        var entries = new List<BoardLaneAutomationEvent>();
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            entries.Add(new(reader.GetString(0), reader.GetInt64(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4), reader.GetBoolean(5)));
        return entries;
    }

    public async Task<bool> IsLaneAutomationCurrentAsync(BoardLaneAutomationEvent entry, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var query = connection.CreateCommand();
        query.CommandText = $"""
            WITH pending AS ({PendingLaneEntriesSql})
            SELECT EXISTS (SELECT 1 FROM pending p JOIN BoardCards c ON c.Id = p.CardId
                WHERE p.CardId = $card AND p.JobId = $job AND p.EventKey = $event
                  AND c.ProjectPath = $project{ProjectPathCollation} AND c.ColumnId = p.ColumnId AND c.DeletedUTC IS NULL
                  AND (EXISTS (SELECT 1 FROM BoardLaneAutomations a WHERE a.ColumnId = p.ColumnId AND a.JobId = p.JobId)
                    OR EXISTS (SELECT 1 FROM BoardLaneAdditionalAutomations a WHERE a.ColumnId = p.ColumnId AND a.JobId = p.JobId)));
            """;
        BindEntry(query, entry);
        query.Parameters.AddWithValue("$project", entry.ProjectPath);
        return Convert.ToInt64(await query.ExecuteScalarAsync(cancellationToken)) != 0;
    }

    public Task AcknowledgeLaneAutomationAsync(BoardLaneAutomationEvent entry, CancellationToken cancellationToken = default) =>
        RecordLaneAutomationDispatchAsync(entry, new("Cancelled", "Lane entry acknowledged without a dispatch result."), DateTime.UtcNow, cancellationToken);

    public async Task RecordLaneAutomationDispatchAsync(BoardLaneAutomationEvent entry, BoardLaneAutomationDispatch dispatch,
        DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await WriteLaneAutomationDispatchAsync(connection, transaction, entry, dispatch, nowUtc, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task WriteLaneAutomationDispatchAsync(SqliteConnection connection, SqliteTransaction transaction,
        BoardLaneAutomationEvent entry, BoardLaneAutomationDispatch dispatch, DateTime nowUtc, CancellationToken cancellationToken)
    {
        if (dispatch.Status is not ("Waiting" or "Queued" or "Skipped" or "Cancelled" or "Failed"))
            throw new ArgumentException("Invalid lane dispatch status.", nameof(dispatch));
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // A late busy observation cannot revive an entry cancelled by a move. A committed run
        // wins over cancellation/skip observations, including a commit racing a lane departure.
        command.CommandText = $"""
            INSERT INTO BoardLaneAutomationDispatch (EventKey, JobId, CardId, ColumnId, DueUnixMs, Status, Reason, RunId, LastAttemptUnixMs)
            SELECT EventKey, JobId, CardId, ColumnId, DueUnixMs, $status, $reason, $run, $now
            FROM ({PendingLaneEntriesSql}) p WHERE p.CardId = $card AND p.JobId = $job AND p.EventKey = $event
            ON CONFLICT(EventKey, JobId) DO UPDATE SET Status = excluded.Status, Reason = excluded.Reason,
                RunId = excluded.RunId, LastAttemptUnixMs = excluded.LastAttemptUnixMs
                WHERE BoardLaneAutomationDispatch.Status = 'Waiting' OR excluded.Status = 'Queued';
            UPDATE BoardLaneAutomationDispatch SET Status = $status, Reason = $reason, RunId = $run, LastAttemptUnixMs = $now
                WHERE CardId = $card AND JobId = $job AND EventKey = $event AND $status = 'Queued';
            """;
        BindEntry(command, entry);
        command.Parameters.AddWithValue("$status", dispatch.Status);
        command.Parameters.AddWithValue("$reason", dispatch.Reason);
        command.Parameters.AddWithValue("$run", (object?)dispatch.RunId ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", new DateTimeOffset(nowUtc).ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(cancellationToken);
        if (dispatch.Status != "Waiting")
        {
            command.CommandText = """
                DELETE FROM BoardPendingAutomations WHERE CardId = $card AND JobId = $job AND EventKey = $event;
                DELETE FROM BoardPendingAdditionalAutomations WHERE CardId = $card AND JobId = $job AND EventKey = $event;
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static void BindEntry(SqliteCommand command, BoardLaneAutomationEvent entry)
    {
        command.Parameters.AddWithValue("$card", entry.CardId);
        command.Parameters.AddWithValue("$job", entry.JobId);
        command.Parameters.AddWithValue("$event", entry.EventKey);
    }
}
