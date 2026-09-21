namespace VibeRails.Services.Board;

public sealed partial class BoardStore
{
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
