using Microsoft.Data.Sqlite;
using VibeRails.Data.Abstractions;
using VibeRails.Data.Sqlite;
using VibeRails.Services.Board;
using Xunit;

namespace Tests.Services.Board;

public sealed class BoardSimplificationMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "board-retirement-" + Guid.NewGuid().ToString("N"));
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ExistingDatabaseRequiresExplicitMigration_AndPreservesHistoryUntilThen()
    {
        var connectionString = await LegacyDatabaseAsync();
        using var policy = SchemaUpgradePolicy.Scope(allowBreaking: false);
        var error = Assert.Throws<StorageException>(() => new BoardStore(connectionString));
        Assert.Contains("board/8", error.Message);
        Assert.Contains("vb --migrate", error.Message);
        using var db = SqliteConnectionFactory.Open(connectionString);
        Assert.Equal(1L, Scalar(db, "SELECT COUNT(*) FROM BoardDescriptionRevisions;"));
        Assert.Equal(2L, Scalar(db, "PRAGMA user_version;"));
    }

    [Fact]
    public async Task MigrationBacksUpOldState_DropsHistoryAndRemovedFiles_PreservesCurrentCardAndRails()
    {
        var connectionString = await LegacyDatabaseAsync();
        using var policy = SchemaUpgradePolicy.Scope(allowBreaking: true, otherProcesses: [], backup: true);
        var store = new BoardStore(connectionString);
        var card = (await store.GetCardDetailAsync(_root, "VB-1", Ct))!;
        Assert.Equal("Current description", card.Card.Description);
        Assert.False(card.Card.Flagged);
        Assert.Single(card.Comments);
        Assert.Single(card.Notes);
        Assert.Single(card.Sessions);
        Assert.Single(card.Attachments);
        Assert.Equal("current.txt", card.Attachments[0].Name);
        Assert.Equal("current", System.Text.Encoding.UTF8.GetString((await store.GetAttachmentContentAsync(_root, "VB-1", "current", Ct))!.Content));
        Assert.Null(await store.GetAttachmentContentAsync(_root, "VB-1", "removed", Ct));
        using var db = SqliteConnectionFactory.Open(connectionString);
        Assert.Equal(0L, Scalar(db, "SELECT COUNT(*) FROM sqlite_schema WHERE name LIKE 'BoardDescription%';"));
        Assert.False(SqliteSchema.HasColumn(db, null, "BoardColumns", "WipLimit"));
        Assert.False(SqliteSchema.HasColumn(db, null, "BoardAttachments", "DeletedUTC"));
        Assert.Equal(1L, Scalar(db, "SELECT COUNT(*) FROM BoardAttachmentContents;"));
        Assert.Null(Scalar(db, "PRAGMA foreign_key_check;"));
        Assert.Equal(3L, Scalar(db, "PRAGMA user_version;"));
        Assert.Throws<StorageException>(() => SqliteMigrationRunner.RequireGenerationAtMost(db, 2, "state.db"));
        var schema = Scalar(db, "PRAGMA schema_version;");
        _ = new BoardStore(connectionString);
        Assert.Equal(schema, Scalar(db, "PRAGMA schema_version;"));
        var backup = Assert.Single(Directory.GetFiles(Path.Combine(_root, "backups"), "*", SearchOption.AllDirectories));
        using var saved = SqliteConnectionFactory.Open(ConnectionString(backup));
        Assert.Equal("Old description", Scalar(saved, "SELECT Description FROM BoardDescriptionRevisions;"));
        Assert.Equal(2L, Scalar(saved, "SELECT COUNT(*) FROM BoardAttachmentContents;"));
    }

    private async Task<string> LegacyDatabaseAsync()
    {
        Directory.CreateDirectory(_root);
        var seed = ConnectionString(Path.Combine(_root, "seed.db"));
        var store = new BoardStore(seed);
        await store.EnsureDefaultColumnsAsync(_root, Ct);
        var card = await store.CreateCardAsync(_root, new(null, "Preserved card", "Current description", null, "medium", null, [], false), Ct);
        await store.AddCommentAsync(_root, card.Id, BoardAuthor.User(), "Comment", Ct);
        await store.AddNoteAsync(_root, card.Id, BoardAuthor.Agent("Codex", "codex", null), "Note", Ct);
        await store.LinkSessionAsync(_root, card.Id, "session", null, "base:codex", "codex", "Session", BoardSessionRecord.LaunchOrigin, Ct);
        using var db = SqliteConnectionFactory.Open(seed);
        using var command = db.CreateCommand();
        command.CommandText = """
            ALTER TABLE BoardColumns ADD COLUMN WipLimit INTEGER;
            UPDATE BoardColumns SET WipLimit=1;
            ALTER TABLE BoardAttachments ADD COLUMN DeletedUTC TEXT;
            ALTER TABLE BoardCards DROP COLUMN Flagged;
            CREATE TABLE BoardDescriptionRevisions(CardId TEXT PRIMARY KEY REFERENCES BoardCards(Id) ON DELETE CASCADE, Description TEXT);
            CREATE TABLE BoardDescriptionSessionEvents(CardId TEXT REFERENCES BoardDescriptionRevisions(CardId) ON DELETE CASCADE);
            CREATE TABLE BoardDescriptionRevisionAttachments(CardId TEXT REFERENCES BoardDescriptionRevisions(CardId) ON DELETE CASCADE, AttachmentId TEXT REFERENCES BoardAttachments(Id) ON DELETE CASCADE);
            INSERT INTO BoardDescriptionRevisions VALUES($card, 'Old description');
            INSERT INTO BoardDescriptionSessionEvents VALUES($card);
            INSERT INTO BoardAttachments(Id,CardId,Name,MimeType,Bytes,DataUrl,CreatedUTC,DeletedUTC)
                VALUES('current',$card,'current.txt','text/plain',7,'','2026-01-01',NULL),
                      ('removed',$card,'removed.txt','text/plain',7,'','2026-01-01','2026-01-02');
            INSERT INTO BoardAttachmentContents VALUES('current',CAST('current' AS BLOB)),('removed',CAST('removed' AS BLOB));
            INSERT INTO BoardDescriptionRevisionAttachments VALUES($card,'removed');
            DELETE FROM SchemaMigrations WHERE Component='board' AND Version>=8;
            PRAGMA user_version=2;
            """;
        command.Parameters.AddWithValue("$card", card.Id);
        command.ExecuteNonQuery();
        // A different file models an installed database present before this process opened it.
        var legacy = ConnectionString(Path.Combine(_root, "legacy.db"));
        using var destination = SqliteConnectionFactory.Open(legacy);
        db.BackupDatabase(destination);
        return legacy;
    }

    private static string ConnectionString(string path) => new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}
