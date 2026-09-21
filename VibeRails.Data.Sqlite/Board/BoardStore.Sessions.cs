using Microsoft.Data.Sqlite;

namespace VibeRails.Services.Board;

public sealed partial class BoardStore
{
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
            WHERE s.SessionId = $session AND c.ProjectPath = $project{ProjectPathCollation};
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
