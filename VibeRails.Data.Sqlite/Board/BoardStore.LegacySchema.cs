using System.Text.Json;
using Microsoft.Data.Sqlite;
using VibeRails.DTOs;

namespace VibeRails.Services.Board;



public sealed partial class BoardStore
{
    // Shipped board/1 SQL only. Board/8 retires these history tables; runtime code never uses them.
    private static void EnsureDescriptionSchema(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS BoardCardOptions (
                CardId TEXT PRIMARY KEY REFERENCES BoardCards(Id) ON DELETE CASCADE,
                OptionsJson TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS BoardDescriptionRevisions (
                CardId TEXT NOT NULL REFERENCES BoardCards(Id) ON DELETE CASCADE,
                Revision INTEGER NOT NULL,
                Description TEXT NOT NULL,
                CreatedUTC TEXT NOT NULL,
                Source TEXT NOT NULL,
                AuthorKind TEXT NOT NULL,
                AuthorLabel TEXT NOT NULL,
                AuthorCli TEXT NULL,
                AuthorSessionId TEXT NULL,
                PRIMARY KEY (CardId, Revision)
            );
            CREATE TABLE IF NOT EXISTS BoardDescriptionSessionEvents (
                CardId TEXT NOT NULL,
                Revision INTEGER NOT NULL,
                SessionId TEXT NOT NULL,
                Kind TEXT NOT NULL,
                Status TEXT NOT NULL,
                CreatedUTC TEXT NOT NULL,
                UpdatedUTC TEXT NOT NULL,
                Message TEXT NULL,
                PRIMARY KEY (CardId, Revision, SessionId, Kind),
                FOREIGN KEY (CardId, Revision) REFERENCES BoardDescriptionRevisions(CardId, Revision) ON DELETE CASCADE
            );
            CREATE TABLE IF NOT EXISTS BoardDescriptionRevisionAttachments (
                CardId TEXT NOT NULL,
                Revision INTEGER NOT NULL,
                AttachmentId TEXT NOT NULL REFERENCES BoardAttachments(Id) ON DELETE CASCADE,
                PRIMARY KEY (CardId, Revision, AttachmentId),
                FOREIGN KEY (CardId, Revision) REFERENCES BoardDescriptionRevisions(CardId, Revision) ON DELETE CASCADE
            );
            """;
        command.ExecuteNonQuery();
    }

}
