using Microsoft.Data.Sqlite;
using VibeRails.Data.Sqlite;

namespace VibeRails.Services.Board;

public sealed partial class BoardStore
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<BoardSessionCard>> GetSessionCardsAsync(IReadOnlyList<string> sessionIds,
        CancellationToken cancellationToken = default)
    {
        var result = new List<BoardSessionCard>();
        if (sessionIds.Count == 0) return result;
        await using var connection = await OpenAsync(cancellationToken);
        foreach (var batch in sessionIds.Distinct(StringComparer.Ordinal).Chunk(100))
        {
            await using var command = connection.CreateCommand();
            var parameters = string.Join(",", batch.Select((_, index) => $"$s{index}"));
            command.CommandText = $"""
                SELECT s.SessionId, c.Id, {CardKeySql}, c.Title, {CardDisplayIdSql}
                FROM {AllSessionsSql} s JOIN BoardCards c ON c.Id = s.CardId
                {CardPrefixJoinSql}
                WHERE s.SessionId IN ({parameters}) AND c.DeletedUTC IS NULL
                ORDER BY s.SessionId, s.LinkOrder, s.CreatedUTC, s.CardId;
                """;
            for (var i = 0; i < batch.Length; i++) command.Parameters.AddWithValue($"$s{i}", batch[i]);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                result.Add(new BoardSessionCard(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4)));
        }
        return result;
    }

    public async Task<IReadOnlySet<string>> GetAutomationSessionIdsAsync(string projectPath,
        IReadOnlyList<string> sessionIds, CancellationToken cancellationToken = default)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (sessionIds.Count == 0) return result;
        await using var state = await OpenStateAsync(cancellationToken);
        // A Board-only/stdio host need not have initialized Jobs. This is a read, not setup.
        if (!SqliteSchema.HasColumn(state, null, "JobRuns", "SessionId"))
            return result;
        var hasTerminalSession = SqliteSchema.HasColumn(state, null, "JobRuns", "TerminalSessionId");
        foreach (var batch in sessionIds.Distinct(StringComparer.Ordinal).Chunk(100))
        {
            await using var command = state.CreateCommand();
            var parameters = string.Join(",", batch.Select((_, index) => $"$s{index}"));
            var terminal = hasTerminalSession ? "TerminalSessionId" : "NULL";
            command.CommandText = $"""
                SELECT SessionId, {terminal} FROM JobRuns
                WHERE ProjectPath = $project{ProjectPathCollation}
                  AND (SessionId IN ({parameters}) OR {terminal} IN ({parameters}));
                """;
            command.Parameters.AddWithValue("$project", NormalizeProjectPath(projectPath));
            for (var i = 0; i < batch.Length; i++) command.Parameters.AddWithValue($"$s{i}", batch[i]);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                for (var i = 0; i < 2; i++)
                    if (!reader.IsDBNull(i)) result.Add(reader.GetString(i));
        }
        return result;
    }

    private static async Task<HashSet<string>> ReadCommitTargetCardsAsync(SqliteConnection connection,
        SqliteTransaction transaction, string project, string cardId, string? sessionId, CancellationToken cancellationToken)
    {
        var targets = new HashSet<string>(StringComparer.Ordinal) { cardId };
        if (string.IsNullOrWhiteSpace(sessionId))
            return targets;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT s.CardId FROM {AllSessionsSql} s JOIN BoardCards c ON c.Id = s.CardId
            WHERE s.SessionId = $session AND c.ProjectPath = $project{ProjectPathCollation} AND c.DeletedUTC IS NULL;
            """;
        command.Parameters.AddWithValue("$session", sessionId);
        command.Parameters.AddWithValue("$project", project);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            targets.Add(reader.GetString(0));
        return targets;
    }

    // Older versions still read/write the original table. If one recreates a primary link
    // already present in the additional table, expose that pair only once, preferring primary.
    private const string AllSessionsSql = """
        (SELECT SessionId, CardId, TabId, Selection, Cli, DisplayName, Origin, CreatedUTC, 0 AS LinkOrder
         FROM BoardCardSessions
         UNION ALL
         SELECT a.SessionId, a.CardId, a.TabId, a.Selection, a.Cli, a.DisplayName, a.Origin, a.CreatedUTC, 1 AS LinkOrder
         FROM BoardAdditionalCardSessions a
         WHERE NOT EXISTS (SELECT 1 FROM BoardCardSessions p WHERE p.SessionId = a.SessionId AND p.CardId = a.CardId))
        """;

    private const string AdditionalCardSessionsSchemaSql = """
        CREATE TABLE IF NOT EXISTS BoardAdditionalCardSessions (
            SessionId TEXT NOT NULL,
            CardId TEXT NOT NULL REFERENCES BoardCards(Id) ON DELETE CASCADE,
            TabId TEXT NULL,
            Selection TEXT NOT NULL,
            Cli TEXT NOT NULL,
            DisplayName TEXT NOT NULL,
            Origin TEXT NOT NULL,
            CreatedUTC TEXT NOT NULL,
            PRIMARY KEY (SessionId, CardId)
        );
        CREATE INDEX IF NOT EXISTS IX_BoardAdditionalCardSessions_Card ON BoardAdditionalCardSessions(CardId);
        """;
}
