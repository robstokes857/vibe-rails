using Microsoft.Data.Sqlite;
using VibeRails.Data.Abstractions;
using VibeRails.Data.Sqlite;
using VibeRails.Services.UserInOut;
using Xunit;

namespace Tests.DB;

/// <summary>
/// The seam no unit test covered on 2026-09-16: the shipped binary and this build writing to one
/// file. The legacy SQL below is copied verbatim from main:VibeRails/DB/SqlStrings.cs (1.10.10),
/// which still inserts into UserInputs_fts directly. When a migration changes what the shipped
/// binary can do, these tests must change with it -- either the old writes stay consistent, or
/// the old binary is refused by the generation gate. Silent drift is the failing answer.
/// </summary>
public sealed class PreviousReleaseCompatibilityTests : IDisposable
{
    private const string LegacyCreateFts =
        "CREATE VIRTUAL TABLE IF NOT EXISTS UserInputs_fts USING fts5(InputText, content='UserInputs', content_rowid='Id', tokenize='porter unicode61');";
    private const string LegacyCreateFtsDeleteTrigger =
        "CREATE TRIGGER IF NOT EXISTS UserInputs_fts_ad AFTER DELETE ON UserInputs BEGIN INSERT INTO UserInputs_fts(UserInputs_fts, rowid, InputText) VALUES('delete', old.Id, old.InputText); END;";
    private const string LegacyBackfillSelect =
        "SELECT Id, InputText FROM UserInputs WHERE Id NOT IN (SELECT id FROM UserInputs_fts_docsize);";
    private const string LegacyInsertFtsRow =
        "INSERT INTO UserInputs_fts(rowid, InputText) VALUES ($rowid, $inputText);";

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"viberails-compat-{Guid.NewGuid():N}.db");
    private string ConnectionString => new SqliteConnectionStringBuilder { DataSource = _path, Pooling = false }.ToString();

    [Fact]
    public async Task LegacyDirectIndexWriteFollowedByTheNewDrainLeavesOneConsistentIndex()
    {
        StateDatabaseSchema.Ensure(ConnectionString);
        using var connection = SqliteConnectionFactory.Open(ConnectionString);
        var id = InsertPrompt(connection, "please deploy the widget service today");
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM UserInputSearchPending;"));

        LegacyStartupAndBackfill(connection);
        // Before the drain: indexed by the old binary, no content row yet -- the exact state seen
        // live at 14:01Z on 2026-09-16.
        Assert.Equal(1L, OrphanedIndexRows(connection));

        var store = new SqliteSearchIndexMaintenanceStore(ConnectionString);
        while (await store.RepairPendingAsync(100, TestContext.Current.CancellationToken) > 0) { }

        AssertIndexIntegrity(connection);
        Assert.Equal(0L, OrphanedIndexRows(connection));
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM UserInputs_fts WHERE UserInputs_fts MATCH 'widget';"));
        Assert.Equal(id, Scalar(connection, "SELECT rowid FROM UserInputs_fts WHERE UserInputs_fts MATCH 'widget';"));
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM UserInputs_fts_docsize;"));
        Assert.False(await store.RepairIndexIfInconsistentAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PromptDeletedBeforeTheDrainLeavesOrphanedPostingsThatMaintenanceRepairs()
    {
        StateDatabaseSchema.Ensure(ConnectionString);
        using var connection = SqliteConnectionFactory.Open(ConnectionString);
        var id = InsertPrompt(connection, "please deploy the widget service today");
        LegacyStartupAndBackfill(connection);

        // The new delete trigger clears the queue and the (absent) content row; the postings the
        // old binary wrote have nothing left to delete them.
        Execute(connection, $"DELETE FROM UserInputs WHERE Id={id};");
        Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM UserInputSearchPending;"));
        Assert.Equal(1L, OrphanedIndexRows(connection));
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM UserInputs_fts WHERE UserInputs_fts MATCH 'widget';"));

        var store = new SqliteSearchIndexMaintenanceStore(ConnectionString);
        Assert.True(await store.RepairIndexIfInconsistentAsync(TestContext.Current.CancellationToken));

        AssertIndexIntegrity(connection);
        Assert.Equal(0L, OrphanedIndexRows(connection));
        Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM UserInputs_fts WHERE UserInputs_fts MATCH 'widget';"));
        Assert.False(await store.RepairIndexIfInconsistentAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ARowIndexedByBothBuildsFailsTheStrictCheckUntilMaintenanceRebuilds()
    {
        StateDatabaseSchema.Ensure(ConnectionString);
        using var connection = SqliteConnectionFactory.Open(ConnectionString);
        var id = InsertPrompt(connection, "please deploy the widget service today");
        LegacyStartupAndBackfill(connection);
        // Second write through the content table, bypassing the drain's own repair: this is what
        // the live database looked like after 14:03Z on 2026-09-16 -- structurally fine, row
        // totals off by one per prompt, and only the strict check can tell.
        Execute(connection, $"INSERT INTO UserInputSearchDocuments(UserInputId, InputText) SELECT {id}, InputText FROM UserInputs WHERE Id={id}; DELETE FROM UserInputSearchPending WHERE UserInputId={id};");
        Execute(connection, "INSERT INTO UserInputs_fts(UserInputs_fts) VALUES('integrity-check');");
        var strict = Assert.Throws<SqliteException>(() => AssertIndexIntegrity(connection));
        Assert.Equal(11, strict.SqliteErrorCode);

        var store = new SqliteSearchIndexMaintenanceStore(ConnectionString);
        Assert.True(await store.RepairIndexIfInconsistentAsync(TestContext.Current.CancellationToken));
        AssertIndexIntegrity(connection);
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM UserInputs_fts WHERE UserInputs_fts MATCH 'widget';"));
        Assert.False(await store.RepairIndexIfInconsistentAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ThisBuildStampsItsGenerationAndRefusesANewerOne()
    {
        StateDatabaseSchema.Ensure(ConnectionString);
        using var connection = SqliteConnectionFactory.Open(ConnectionString);
        Assert.Equal((long)StateDatabaseSchema.Generation, Scalar(connection, "PRAGMA user_version;"));
        // 1.10.10 reads user_version < 1 as "drop and rebuild the FTS table"; every generation
        // this code ever writes must keep it from doing that.
        Assert.True(StateDatabaseSchema.Generation >= 1);

        Execute(connection, "PRAGMA user_version=99;");
        var refused = Assert.Throws<StorageException>(() => StateDatabaseSchema.Ensure(ConnectionString));
        Assert.Contains("newer VibeRails", refused.Message);
        Assert.Contains("generation 99", refused.Message);
        Assert.Equal(99L, Scalar(connection, "PRAGMA user_version;"));
    }

    private static long InsertPrompt(SqliteConnection connection, string text)
    {
        Execute(connection, """
            INSERT INTO Sessions(Id, Cli, WorkingDirectory, StartedUTC)
            VALUES ('legacy-session', 'Claude', 'C:\repo', '2026-09-16T00:00:00Z');
            """);
        using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO UserInputs(SessionId, Sequence, InputText, TimestampUTC)
            VALUES ('legacy-session', 1, $text, '2026-09-16T00:00:01Z');
            SELECT last_insert_rowid();
            """;
        insert.Parameters.AddWithValue("$text", text);
        return Convert.ToInt64(insert.ExecuteScalar());
    }

    /// <summary>What 1.10.10's EnsureInitialized and InsertUserInputAsync do against this file.</summary>
    private static void LegacyStartupAndBackfill(SqliteConnection connection)
    {
        Execute(connection, LegacyCreateFts);
        Execute(connection, LegacyCreateFtsDeleteTrigger);
        var rows = new List<(long Id, string Text)>();
        using (var select = connection.CreateCommand())
        {
            select.CommandText = LegacyBackfillSelect;
            using var reader = select.ExecuteReader();
            while (reader.Read())
                rows.Add((reader.GetInt64(0), reader.GetString(1)));
        }
        Assert.NotEmpty(rows);
        foreach (var (id, text) in rows)
        {
            var safe = InputEtlFilter.Process(text);
            if (string.IsNullOrWhiteSpace(safe))
                continue;
            using var insert = connection.CreateCommand();
            insert.CommandText = LegacyInsertFtsRow;
            insert.Parameters.AddWithValue("$rowid", id);
            insert.Parameters.AddWithValue("$inputText", safe);
            insert.ExecuteNonQuery();
        }
    }

    private static long OrphanedIndexRows(SqliteConnection connection) => Convert.ToInt64(Scalar(connection, """
        SELECT COUNT(*) FROM UserInputs_fts_docsize AS indexed
        WHERE NOT EXISTS (SELECT 1 FROM UserInputSearchDocuments AS content WHERE content.UserInputId = indexed.id);
        """));

    /// <summary>
    /// rank=1 makes FTS5 compare the index with its content table (row totals, docsizes,
    /// postings). The plain form only checks structure and passes over a double-inserted row.
    /// </summary>
    private static void AssertIndexIntegrity(SqliteConnection connection) =>
        Execute(connection, "INSERT INTO UserInputs_fts(UserInputs_fts, rank) VALUES('integrity-check', 1);");

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var path in new[] { _path, _path + "-wal", _path + "-shm" })
            File.Delete(path);
    }
}
