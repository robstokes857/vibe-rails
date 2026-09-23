using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;

namespace VibeRails.Services.Board;

public sealed partial class BoardStore
{
    private const string CardPageFilterSql = """
        ($q = '' OR instr(lower(
            {KEY} || ' ' || c.Title || ' ' || c.Description || ' ' || c.Type || ' ' ||
            CASE c.Type WHEN 'research-spike' THEN 'Research spike' WHEN 'chore' THEN 'Chore / tech debt' ELSE c.Type END || ' ' ||
            COALESCE((SELECT group_concat(value, ' ') FROM json_each(c.Tags)), '')
        ), lower($q)) > 0)
        AND ($assignee = '' OR replace(lower(c.Assignee), ':grok-4.6', ':grok') = $assignee)
        AND ($type = '' OR c.Type = $type)
        AND ($priority = '' OR c.Priority = $priority)
        AND ($tag = '' OR EXISTS (SELECT 1 FROM json_each(c.Tags) WHERE value = $tag))
        """;

    public async Task<BoardCardPage> GetCardsPageAsync(string projectPath, BoardCardPageQuery query,
        CancellationToken cancellationToken = default, string? boardId = null)
    {
        var project = NormalizeProjectPath(projectPath);
        query = query with { PageSize = Math.Clamp(query.PageSize, 1, 100), Offset = Math.Max(0, query.Offset) };
        await using var connection = await OpenAsync(cancellationToken);
        // Counts, page membership and filter choices share one read snapshot without taking
        // the writer lock. The limit selects ids before loading descriptions and card rails.
        await using var transaction = connection.BeginTransaction(deferred: true);
        var board = await ResolveBoardIdAsync(connection, transaction, project, boardId, cancellationToken);
        if (board is null) return new([], [], [], [], 0, 0, 0, 0);
        var columns = await ReadColumnsAsync(connection, transaction, project, board, cancellationToken);
        if (query.ColumnId is not null && !columns.Any(column => column.Id == query.ColumnId))
            throw new BoardValidationException("Lane not found on this board.");

        var filter = CardPageFilterSql.Replace("{KEY}", $"{CardPrefixSql} || '-' || c.Number", StringComparison.Ordinal);
        var counts = new Dictionary<string, (int Total, int Filtered, int Blocked, long Points)>();
        await using (var count = connection.CreateCommand())
        {
            count.Transaction = transaction;
            count.CommandText = $"""
                SELECT c.ColumnId, COUNT(*),
                       SUM(CASE WHEN {filter} THEN 1 ELSE 0 END),
                       SUM(CASE WHEN {filter} THEN c.Blocked ELSE 0 END),
                       SUM(CASE WHEN {filter} THEN COALESCE(c.Points, 0) ELSE 0 END)
                FROM BoardCards c
                {CardPrefixJoinSql}
                WHERE c.ProjectPath = $project{ProjectPathCollation}
                  AND c.ColumnId IN (SELECT Id FROM BoardColumns WHERE BoardId = $board)
                GROUP BY c.ColumnId;
                """;
            AddPageParameters(count, project, board, query);
            await using var reader = await count.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                counts[reader.GetString(0)] = (reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetInt64(4));
        }

        var cards = new List<BoardCardRecord>();
        var lanes = new List<BoardCardLanePage>();
        foreach (var column in columns.Where(column => query.ColumnId is null || column.Id == query.ColumnId))
        {
            var count = counts.GetValueOrDefault(column.Id);
            var offset = query.ColumnId is null ? 0 : query.Offset;
            var limit = query.ColumnId is not null || IsCompletedLane(column.Name) ? query.PageSize : -1;
            var continuationToken = limit < 0 ? null : await ReadContinuationTokenAsync(connection, transaction,
                project, board, column.Id, filter, query, cancellationToken);
            var restartRequired = offset > 0 && query.ContinuationToken is not null
                && !string.Equals(query.ContinuationToken, continuationToken, StringComparison.Ordinal);
            // A changed ordering invalidates the offset. Return the new first page as a safe
            // fallback for older clients; current clients see RestartRequired and refresh all
            // lane/card metadata before continuing with the new token.
            var effectiveOffset = restartRequired ? 0 : offset;
            var before = cards.Count;
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = CardSelectSql + $"""
                 WHERE c.Id IN (
                     SELECT c.Id FROM BoardCards c
                     {CardPrefixJoinSql}
                     WHERE c.ProjectPath = $project{ProjectPathCollation}
                       AND c.ColumnId = $column AND {filter}
                     ORDER BY c.Position, c.Number
                     LIMIT $limit OFFSET $offset)
                 ORDER BY c.Position, c.Number;
                """;
            AddPageParameters(command, project, board, query);
            command.Parameters.AddWithValue("$column", column.Id);
            command.Parameters.AddWithValue("$limit", limit);
            command.Parameters.AddWithValue("$offset", effectiveOffset);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                cards.Add(ReadCard(reader));
            var next = Math.Min(count.Filtered, (long)effectiveOffset + cards.Count - before);
            lanes.Add(new(column.Id, count.Total, count.Filtered, (int)next, next < count.Filtered,
                continuationToken, restartRequired));
        }

        var assignees = await ReadPageChoicesAsync(connection, transaction, project, board,
            "SELECT DISTINCT c.Assignee FROM BoardCards c", "c.Assignee IS NOT NULL AND c.Assignee <> ''", cancellationToken);
        var tags = await ReadPageChoicesAsync(connection, transaction, project, board,
            "SELECT DISTINCT tag.value FROM BoardCards c, json_each(c.Tags) tag", "1 = 1", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(cards, lanes, assignees, tags, counts.Values.Sum(c => c.Total), counts.Values.Sum(c => c.Filtered),
            counts.Values.Sum(c => c.Blocked), columns.Where(c => !IsCompletedLane(c.Name)).Sum(c => counts.GetValueOrDefault(c.Id).Points));
    }

    private static void AddPageParameters(SqliteCommand command, string project, string board, BoardCardPageQuery query)
    {
        command.Parameters.AddWithValue("$project", project);
        command.Parameters.AddWithValue("$board", board);
        command.Parameters.AddWithValue("$q", query.Q ?? "");
        command.Parameters.AddWithValue("$assignee", query.Assignee?.ToLowerInvariant() ?? "");
        command.Parameters.AddWithValue("$type", query.Type ?? "");
        command.Parameters.AddWithValue("$priority", query.Priority ?? "");
        command.Parameters.AddWithValue("$tag", query.Tag ?? "");
    }

    private static async Task<string> ReadContinuationTokenAsync(SqliteConnection connection, SqliteTransaction transaction,
        string project, string board, string columnId, string filter, BoardCardPageQuery query,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT c.Id FROM BoardCards c
            {CardPrefixJoinSql}
            WHERE c.ProjectPath = $project{ProjectPathCollation}
              AND c.ColumnId = $column AND {filter}
            ORDER BY c.Position, c.Number;
            """;
        AddPageParameters(command, project, board, query);
        command.Parameters.AddWithValue("$column", columnId);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(reader.GetString(0)));
            hash.AppendData([0]);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static async Task<IReadOnlyList<string>> ReadPageChoicesAsync(SqliteConnection connection, SqliteTransaction transaction,
        string project, string board, string select, string predicate, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = select + $"""
             WHERE c.ProjectPath = $project{ProjectPathCollation}
               AND c.ColumnId IN (SELECT Id FROM BoardColumns WHERE BoardId = $board)
               AND {predicate}
             ORDER BY 1;
            """;
        command.Parameters.AddWithValue("$project", project);
        command.Parameters.AddWithValue("$board", board);
        var values = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) values.Add(reader.GetString(0));
        return values;
    }

    private static bool IsCompletedLane(string name) =>
        name.Contains("ship", StringComparison.OrdinalIgnoreCase)
        || name.Contains("done", StringComparison.OrdinalIgnoreCase)
        || name.Contains("complete", StringComparison.OrdinalIgnoreCase);
}
