using Microsoft.Data.Sqlite;
using VibeRails.Data.Abstractions;
using VibeRails.Data.Sqlite;
using Xunit;

namespace Tests.DB;

public sealed class SearchIndexMaintenanceStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "search-index-repair-" + Guid.NewGuid().ToString("N"));
    private readonly string _connectionString;

    public SearchIndexMaintenanceStoreTests()
    {
        Directory.CreateDirectory(_directory);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(_directory, "state.db") }.ToString();
        StateDatabaseSchema.Ensure(_connectionString);
        Execute("INSERT INTO Sessions(Id, Cli, WorkingDirectory, StartedUTC) VALUES ('test-session', 'test', '.', '2026-09-16T00:00:00Z');");
    }

    [Fact]
    public async Task Repair_FiltersUnsafeInputs_AndCompletesOnlyRequestedBatch()
    {
        Seed(1, "> searchable migration text");
        Seed(2, "sk-abcdefghijklmnopqrstuvwxyz123456");
        Seed(3, "ls");
        Seed(4, "searchable next batch");
        var store = new SqliteSearchIndexMaintenanceStore(_connectionString);

        Assert.Equal(3, await store.RepairPendingAsync(3, TestContext.Current.CancellationToken));
        Assert.Equal(1L, Scalar("SELECT count(*) FROM UserInputs_fts WHERE UserInputs_fts MATCH 'searchable';"));
        Assert.Equal(1L, Scalar("SELECT count(*) FROM UserInputSearchPending;"));
        Assert.Equal(4L, Scalar("SELECT count(*) FROM UserInputs;"));
        Assert.Equal(1L, Scalar("SELECT count(*) FROM UserInputs_fts_docsize;"));

        Assert.Equal(1, await store.RepairPendingAsync(100, TestContext.Current.CancellationToken));
        Assert.Equal(0, await store.RepairPendingAsync(100, TestContext.Current.CancellationToken));
        Assert.Equal(2L, Scalar("SELECT count(*) FROM UserInputs_fts_docsize;"));
    }

    [Fact]
    public async Task Repair_AlreadyIndexedRows_AreIdempotent()
    {
        Seed(1, "searchable migration text");
        Execute("INSERT INTO UserInputSearchDocuments(UserInputId, InputText) VALUES (1, 'searchable migration text');");
        var store = new SqliteSearchIndexMaintenanceStore(_connectionString);
        Assert.Equal(1, await store.RepairPendingAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(1L, Scalar("SELECT count(*) FROM UserInputs_fts WHERE UserInputs_fts MATCH 'searchable';"));
        Assert.Equal(0L, Scalar("SELECT count(*) FROM UserInputSearchPending;"));
    }

    [Fact]
    public async Task Repair_FailureKeepsRawInputAndQueue_RetrySucceeds()
    {
        Seed(1, "searchable migration text");
        Execute("DROP TABLE UserInputs_fts;");
        var store = new SqliteSearchIndexMaintenanceStore(_connectionString);
        await Assert.ThrowsAsync<StorageException>(() => store.RepairPendingAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(1L, Scalar("SELECT count(*) FROM UserInputs;"));
        Assert.Equal(1L, Scalar("SELECT count(*) FROM UserInputSearchPending;"));

        Execute("CREATE VIRTUAL TABLE UserInputs_fts USING fts5(InputText, content='UserInputSearchDocuments', content_rowid='UserInputId');");
        Assert.Equal(1, await store.RepairPendingAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(1L, Scalar("SELECT count(*) FROM UserInputs_fts WHERE UserInputs_fts MATCH 'searchable';"));
    }

    [Fact]
    public async Task Repair_ConcurrentWorkers_DoNotIndexTheSameRowTwice()
    {
        for (var id = 1; id <= 12; id++) Seed(id, $"searchable message {id}");
        var store = new SqliteSearchIndexMaintenanceStore(_connectionString);
        var completed = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(
            () => store.RepairPendingAsync(4, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken)));
        Assert.Equal(12, completed.Sum());
        Assert.Equal(12L, Scalar("SELECT count(*) FROM UserInputs_fts WHERE UserInputs_fts MATCH 'searchable';"));
        Assert.Equal(0L, Scalar("SELECT count(*) FROM UserInputSearchPending;"));
    }

    [Fact]
    public async Task Repair_InputChangesToSecret_RemovesOnlyDerivedSearchText()
    {
        Seed(1, "> searchable migration text");
        var store = new SqliteSearchIndexMaintenanceStore(_connectionString);
        await store.RepairPendingAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("searchable migration text", Scalar("SELECT InputText FROM UserInputSearchDocuments WHERE UserInputId=1;"));

        Execute("UPDATE UserInputs SET InputText='sk-abcdefghijklmnopqrstuvwxyz123456' WHERE Id=1;");
        Assert.Equal(1, await store.RepairPendingAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(0L, Scalar("SELECT count(*) FROM UserInputSearchDocuments;"));
        Assert.Equal(0L, Scalar("SELECT count(*) FROM UserInputs_fts WHERE UserInputs_fts MATCH 'searchable';"));
        Assert.Equal(1L, Scalar("SELECT count(*) FROM UserInputs;"));
        Execute("DELETE FROM UserInputs WHERE Id=1;");
        Execute("INSERT INTO UserInputs_fts(UserInputs_fts) VALUES ('integrity-check');");
    }

    private void Seed(long id, string text)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO UserInputs(Id, SessionId, Sequence, InputText, TimestampUTC)
                VALUES ($id, 'test-session', $id, $text, '2026-09-16T00:00:00Z');
            INSERT OR IGNORE INTO UserInputSearchPending(UserInputId) VALUES ($id);
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$text", text);
        command.ExecuteNonQuery();
    }

    private object? Scalar(string sql)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private void Execute(string sql)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
    }
}
