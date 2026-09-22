using Microsoft.Data.Sqlite;

namespace VibeRails.Services.Board;

public sealed partial class BoardStore
{
    public async Task<IReadOnlyList<BoardLaneAutomationEvent>> GetDueLaneAutomationsAsync(
        DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: true);
        var entries = new List<BoardLaneAutomationEvent>();
        var now = new DateTimeOffset(nowUtc).ToUnixTimeMilliseconds();

        // The scheduler polls this every few seconds and the queue is almost always empty, so the
        // common case must be one indexed probe rather than two three-way joins.
        await using (var any = connection.CreateCommand())
        {
            any.Transaction = transaction;
            any.CommandText = """
                SELECT EXISTS(SELECT 1 FROM BoardPendingAutomations WHERE DueUnixMs <= $now)
                    OR EXISTS(SELECT 1 FROM BoardPendingAdditionalAutomations WHERE DueUnixMs <= $now);
                """;
            any.Parameters.AddWithValue("$now", now);
            if (Convert.ToInt64(await any.ExecuteScalarAsync(cancellationToken)) == 0)
            {
                await transaction.CommitAsync(cancellationToken);
                return entries;
            }
        }

        foreach (var additional in new[] { false, true })
        {
            var pending = additional ? "BoardPendingAdditionalAutomations" : "BoardPendingAutomations";
            var settings = additional ? "BoardLaneAdditionalAutomations" : "BoardLaneAutomations";
            await using var query = connection.CreateCommand();
            query.Transaction = transaction;
            query.CommandText = $"""
                SELECT p.CardId, p.JobId, p.EventKey, c.ProjectPath,
                    'board-lane:' || {CardPrefixSql} || '-' || c.Number || ':' || p.ColumnId || ':' || p.EventKey,
                    c.ColumnId = p.ColumnId AND a.JobId = p.JobId
                FROM {pending} p
                JOIN BoardCards c ON c.Id = p.CardId
                {CardPrefixJoinSql}
                LEFT JOIN {settings} a ON a.ColumnId = p.ColumnId AND a.JobId = p.JobId
                WHERE p.DueUnixMs <= $now ORDER BY p.DueUnixMs, p.CardId, p.JobId LIMIT 100;
                """;
            query.Parameters.AddWithValue("$now", now);
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                entries.Add(new(reader.GetString(0), reader.GetInt64(1), reader.GetString(2),
                    reader.GetString(3), reader.GetString(4), !reader.IsDBNull(5) && reader.GetBoolean(5)));
        }
        await transaction.CommitAsync(cancellationToken);
        return entries;
    }

    public async Task AcknowledgeLaneAutomationAsync(BoardLaneAutomationEvent entry,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var delete = connection.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText = """
            DELETE FROM BoardPendingAutomations WHERE CardId = $card AND JobId = $job AND EventKey = $event;
            DELETE FROM BoardPendingAdditionalAutomations WHERE CardId = $card AND JobId = $job AND EventKey = $event;
            """;
        delete.Parameters.AddWithValue("$card", entry.CardId);
        delete.Parameters.AddWithValue("$job", entry.JobId);
        delete.Parameters.AddWithValue("$event", entry.EventKey);
        await delete.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
