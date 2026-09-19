using Microsoft.Data.Sqlite;
using VibeRails.Data.Sqlite;
using VibeRails.Data.Abstractions;
using VibeRails.DB;
using VibeRails.Services.Board;
using Xunit;

namespace Tests.DB;

public sealed class StoreSchemaAdoptionTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "store-schema-adoption-" + Guid.NewGuid().ToString("N") + ".db");
    private string ConnectionString => new SqliteConnectionStringBuilder { DataSource = _path, Pooling = false }.ToString();

    [Fact]
    public void ReopeningBoardAndJobsDoesNotWriteTheSchemaAgain()
    {
        StateDatabaseSchema.Ensure(ConnectionString);
        _ = new BoardStore(ConnectionString);
        _ = new JobStore(ConnectionString);
        var version = Scalar("PRAGMA schema_version;");
        var migrations = Scalar("SELECT COUNT(*) FROM SchemaMigrations;");

        for (var i = 0; i < 3; i++)
        {
            _ = new BoardStore(ConnectionString);
            _ = new JobStore(ConnectionString);
        }

        Assert.Equal(version, Scalar("PRAGMA schema_version;"));
        Assert.Equal(migrations, Scalar("SELECT COUNT(*) FROM SchemaMigrations;"));
        Assert.Equal(6L, Scalar("SELECT COUNT(*) FROM SchemaMigrations WHERE Component='board';"));
        Assert.Equal(3L, Scalar("SELECT COUNT(*) FROM SchemaMigrations WHERE Component LIKE 'jobs%';"));
    }

    [Fact]
    public async Task JobsConstructedBeforeStateInstallTheirDeferredSessionLink()
    {
        var store = new JobStore(ConnectionString);
        Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM sqlite_schema WHERE name='Sessions_LinkJobRunSession';"));
        // Represents a separate process bringing the shared state schema online later.
        Execute(SqlStrings.CreateEnvironmentsTable + ";" + SqlStrings.CreateSessionsTable + ";");
        Assert.True(await store.TryAcquireOrRenewSchedulerLeaseAsync("late-state", DateTime.UtcNow,
            TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));

        using var connection = SqliteConnectionFactory.Open(ConnectionString);
        Assert.True(JobStore.IsLinkTriggerCurrent(connection));
        var version = Scalar("PRAGMA schema_version;");
        Assert.True(await store.TryAcquireOrRenewSchedulerLeaseAsync("late-state", DateTime.UtcNow,
            TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        Assert.Equal(version, Scalar("PRAGMA schema_version;"));
        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM SchemaMigrations WHERE Component='jobs-session-link';"));
    }

    [Fact]
    public void LegacyBoardAdoptionPreservesCardsAttachmentsAndSeedsHistoryOnce()
    {
        // Create the old board layout, without a migration receipt or the newer history/assets.
        Execute("""
            CREATE TABLE BoardColumns(Id TEXT PRIMARY KEY,ProjectPath TEXT NOT NULL,Name TEXT NOT NULL,
                WipLimit INTEGER,Position INTEGER NOT NULL,Color TEXT NOT NULL,CreatedUTC TEXT NOT NULL,UpdatedUTC TEXT NOT NULL);
            CREATE TABLE BoardCards(Id TEXT PRIMARY KEY,ProjectPath TEXT NOT NULL,Number INTEGER NOT NULL,
                ColumnId TEXT NOT NULL REFERENCES BoardColumns(Id),Position INTEGER NOT NULL,Title TEXT NOT NULL,
                Description TEXT NOT NULL DEFAULT '',Assignee TEXT,Priority TEXT NOT NULL DEFAULT 'medium',
                Points INTEGER,Tags TEXT NOT NULL DEFAULT '[]',Blocked INTEGER NOT NULL DEFAULT 0,
                CreatedUTC TEXT NOT NULL,UpdatedUTC TEXT NOT NULL,UNIQUE(ProjectPath,Number));
            CREATE TABLE BoardAttachments(Id TEXT PRIMARY KEY,CardId TEXT NOT NULL REFERENCES BoardCards(Id) ON DELETE CASCADE,
                Name TEXT NOT NULL,MimeType TEXT NOT NULL,Bytes INTEGER NOT NULL,DataUrl TEXT NOT NULL,CreatedUTC TEXT NOT NULL);
            INSERT INTO BoardColumns VALUES('col','project','Backlog',NULL,0,'#ffffff','2026-01-01','2026-01-01');
            INSERT INTO BoardCards(Id,ProjectPath,Number,ColumnId,Position,Title,Description,CreatedUTC,UpdatedUTC)
                VALUES('card','project',9,'col',0,'Preserved title','Preserved requirements','2026-01-01','2026-01-01');
            INSERT INTO BoardAttachments VALUES('asset','card','old.png','image/png',1,'data:image/png;base64,AA==','2026-01-01');
            """);

        _ = new BoardStore(ConnectionString);
        Assert.Equal("Preserved requirements", Scalar("SELECT Description FROM BoardCards WHERE Id='card';"));
        Assert.Equal("data:image/png;base64,AA==", Scalar("SELECT DataUrl FROM BoardAttachments WHERE Id='asset';"));
        Assert.Equal(9L, Scalar("SELECT LastNumber FROM BoardCardSequences WHERE ProjectPath='project';"));
        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM BoardDescriptionRevisions WHERE CardId='card';"));
        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM BoardDescriptionRevisionAttachments WHERE AttachmentId='asset';"));
        var version = Scalar("PRAGMA schema_version;");
        _ = new BoardStore(ConnectionString);
        Assert.Equal(version, Scalar("PRAGMA schema_version;"));
        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM BoardDescriptionRevisions WHERE CardId='card';"));
        Assert.Null(Scalar("PRAGMA foreign_key_check;"));
    }

    [Fact]
    public void FailedBoardAdoptionRollsBackWithoutWritingReceiptOrLosingLegacyRows()
    {
        Execute("CREATE TABLE BoardCards(Id TEXT PRIMARY KEY,Title TEXT); INSERT INTO BoardCards VALUES('kept','source row');");
        var failure = Assert.Throws<StorageException>(() => new BoardStore(ConnectionString));
        Assert.IsType<SqliteException>(failure.InnerException);
        Assert.Equal("source row", Scalar("SELECT Title FROM BoardCards WHERE Id='kept';"));
        Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM sqlite_schema WHERE name IN ('SchemaMigrations','BoardColumns');"));
    }

    private object? Scalar(string sql)
    {
        using var connection = SqliteConnectionFactory.Open(ConnectionString);
        using var query = connection.CreateCommand();
        query.CommandText = sql;
        return query.ExecuteScalar();
    }

    private void Execute(string sql)
    {
        using var connection = SqliteConnectionFactory.Open(ConnectionString);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        foreach (var path in new[] { _path, _path + "-wal", _path + "-shm" }) File.Delete(path);
    }
}
