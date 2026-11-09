using VibeRails.Data.Sqlite;

namespace Tests.Services.Board;

/// <summary>The original shipped deletion schema, independent of today's migration SQL.</summary>
internal static class BoardDeletionUpgradeFixture
{
    internal static async Task RestoreOriginalSchemaAsync(string connectionString, CancellationToken ct)
    {
        // Only disposable test databases reach this helper. Preserve the applied /1 and /2
        // receipts: a release must upgrade this table without replaying either migration.
        await using var db = SqliteConnectionFactory.Open(connectionString);
        await using var command = db.CreateCommand();
        command.CommandText = """
            DROP TABLE BoardDeletedCards;
            CREATE TABLE BoardDeletedCards (
                CardId TEXT PRIMARY KEY,
                CardKey TEXT NOT NULL COLLATE NOCASE UNIQUE,
                ProjectPath TEXT NOT NULL,
                Number INTEGER NOT NULL,
                BoardId TEXT NOT NULL,
                DeletedUTC TEXT NOT NULL,
                EntryId TEXT NOT NULL UNIQUE,
                AuthorKind TEXT NOT NULL,
                AuthorLabel TEXT NOT NULL,
                AuthorCli TEXT,
                RemoteSeq INTEGER,
                Managed INTEGER NOT NULL
            );
            CREATE INDEX IX_BoardDeletedCards_Outbox ON BoardDeletedCards(BoardId, RemoteSeq);
            CREATE TRIGGER BoardDeletedCards_RecordSyncOrder AFTER INSERT ON BoardDeletedCards
            BEGIN
                INSERT INTO BoardSyncEntryOrder (EntryId) VALUES (NEW.EntryId);
            END;
            CREATE TRIGGER BoardDeletedCards_RemoveSyncOrder AFTER DELETE ON BoardDeletedCards
            BEGIN
                DELETE FROM BoardSyncEntryOrder WHERE EntryId = OLD.EntryId;
            END;
            DELETE FROM SchemaMigrations WHERE Component = 'board-card-deletion' AND Version > 2;
            """;
        await command.ExecuteNonQueryAsync(ct);
    }
}
