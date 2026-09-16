using Microsoft.Data.Sqlite;
using VibeRails.Data.Sqlite;
using VibeRails.Data.Abstractions;
using Xunit;

namespace Tests.DB;

public sealed class SqliteMigrationRunnerTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"viberails-migrations-{Guid.NewGuid():N}.db");
    private string ConnectionString => new SqliteConnectionStringBuilder { DataSource = _path, Pooling = false }.ToString();

    [Fact]
    public void FailedMigrationRollsBackSchemaAndCompletionRecord()
    {
        using var connection = SqliteConnectionFactory.Open(ConnectionString);
        Assert.Throws<InvalidOperationException>(() => SqliteMigrationRunner.Apply(connection, "test", 1, (db, tx) =>
        {
            Execute(db, tx, "CREATE TABLE Example(Id INTEGER);");
            throw new InvalidOperationException("simulated interruption");
        }));
        using var check = connection.CreateCommand();
        check.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE name='Example';";
        Assert.Equal(0L, check.ExecuteScalar());
        Assert.True(SqliteMigrationRunner.Apply(connection, "test", 1,
            (db, tx) => Execute(db, tx, "CREATE TABLE Example(Id INTEGER);")));
        Assert.False(SqliteMigrationRunner.Apply(connection, "test", 1,
            (_, _) => throw new InvalidOperationException("already applied")));
    }

    [Fact]
    public void FailedSecondMigrationPreservesCommittedFirstVersionAndRetriesCleanly()
    {
        using var connection = SqliteConnectionFactory.Open(ConnectionString);
        Assert.True(SqliteMigrationRunner.Apply(connection, "upgrade", 1, (db, tx) =>
            Execute(db, tx, "CREATE TABLE Preserved(Id INTEGER PRIMARY KEY, Value TEXT); INSERT INTO Preserved VALUES(1,'original');")));

        Assert.Throws<InvalidOperationException>(() => SqliteMigrationRunner.Apply(connection, "upgrade", 2, (db, tx) =>
        {
            Execute(db, tx, "ALTER TABLE Preserved ADD COLUMN Added TEXT; UPDATE Preserved SET Value='partial';");
            throw new InvalidOperationException("failed upgrade");
        }));

        using var check = connection.CreateCommand();
        check.CommandText = "SELECT Value FROM Preserved WHERE Id=1;";
        Assert.Equal("original", check.ExecuteScalar());
        check.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Preserved') WHERE name='Added';";
        Assert.Equal(0L, check.ExecuteScalar());
        check.CommandText = "SELECT group_concat(Version) FROM SchemaMigrations WHERE Component='upgrade';";
        Assert.Equal("1", check.ExecuteScalar());
        Assert.False(SqliteMigrationRunner.Apply(connection, "upgrade", 1, (_, _) => Assert.Fail("reran version 1")));

        Assert.True(SqliteMigrationRunner.Apply(connection, "upgrade", 2, (db, tx) =>
            Execute(db, tx, "ALTER TABLE Preserved ADD COLUMN Added TEXT; UPDATE Preserved SET Added='complete';")));
        check.CommandText = "SELECT Value || ':' || Added FROM Preserved WHERE Id=1;";
        Assert.Equal("original:complete", check.ExecuteScalar());
        check.CommandText = "SELECT COUNT(*) FROM SchemaMigrations WHERE Component='upgrade';";
        Assert.Equal(2L, check.ExecuteScalar());
    }

    [Fact]
    public async Task IndependentConnectionsApplyMigrationOnlyOnce()
    {
        var applied = 0;
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            using var connection = SqliteConnectionFactory.Open(ConnectionString);
            SqliteMigrationRunner.Apply(connection, "concurrent", 1, (db, tx) =>
            {
                Execute(db, tx, "CREATE TABLE OnceOnly(Value INTEGER); INSERT INTO OnceOnly VALUES (42);");
                Interlocked.Increment(ref applied);
            });
        })));
        Assert.Equal(1, applied);
        using var reopened = SqliteConnectionFactory.Open(ConnectionString);
        using var check = reopened.CreateCommand();
        check.CommandText = "PRAGMA schema_version;";
        var schemaVersion = check.ExecuteScalar();
        Assert.False(SqliteMigrationRunner.Apply(reopened, "concurrent", 1, (_, _) => Assert.Fail("reran")));
        Assert.Equal(schemaVersion, check.ExecuteScalar());
    }

    [Fact]
    public void FileConnectionsUsePrivateCacheAndEnforceForeignKeys()
    {
        using var connection = SqliteConnectionFactory.Open(ConnectionString + ";Cache=Shared");
        Assert.Equal(SqliteCacheMode.Private, new SqliteConnectionStringBuilder(connection.ConnectionString).Cache);
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys;";
        Assert.Equal(1L, command.ExecuteScalar());
        command.CommandText = "PRAGMA busy_timeout;";
        Assert.Equal(5000L, command.ExecuteScalar());
    }

    [Fact]
    public async Task PendingMigrationWaitsPastNormalTimeoutAndConcurrentWaitersApplyOnlyOnce()
    {
        using var writer = SqliteConnectionFactory.Open(ConnectionString);
        SqliteMigrationRunner.Apply(writer, "seed", 1, (db, tx) => Execute(db, tx, "CREATE TABLE Seed(Id INTEGER);"));
        using var blocker = writer.BeginTransaction(deferred: false);
        var applied = 0;
        var started = 0;
        var allStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workers = Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
        {
            using var connection = SqliteConnectionFactory.Open(ConnectionString);
            if (Interlocked.Increment(ref started) == 2) allStarted.SetResult();
            var result = SqliteMigrationRunner.Apply(connection, "contended", 1, (db, tx) =>
            {
                Execute(db, tx, "CREATE TABLE Waited(Id INTEGER);");
                Interlocked.Increment(ref applied);
            });
            Assert.Equal(5, connection.DefaultTimeout);
            using var pragma = connection.CreateCommand();
            pragma.CommandText = "PRAGMA busy_timeout;";
            Assert.Equal(5000L, pragma.ExecuteScalar());
            return result;
        })).ToArray();
        await allStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            // Both pending migrations must survive an older process holding the writer longer
            // than the normal five-second store timeout. No migration callback is retried.
            await Task.Delay(TimeSpan.FromMilliseconds(6500), TestContext.Current.CancellationToken);
            Assert.All(workers, worker => Assert.False(worker.IsCompleted));
        }
        finally
        {
            blocker.Rollback();
        }
        var results = await Task.WhenAll(workers);
        Assert.Equal(1, applied);
        Assert.Single(results, value => value);
    }

    [Fact]
    public void PendingMigrationTimeoutRestoresNormalPolicyAndLeavesNoReceipt()
    {
        using var writer = SqliteConnectionFactory.Open(ConnectionString);
        SqliteMigrationRunner.Apply(writer, "seed", 1, (db, tx) => Execute(db, tx, "CREATE TABLE Seed(Id INTEGER);"));
        using var blocker = writer.BeginTransaction(deferred: false);
        using var waiting = SqliteConnectionFactory.Open(ConnectionString);
        var called = false;
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var failure = Assert.Throws<StorageException>(() => SqliteMigrationRunner.Apply(
            waiting, "blocked", 7, (_, _) => called = true, lockTimeoutSeconds: 1));
        elapsed.Stop();
        Assert.False(called);
        Assert.True(failure.IsTransient);
        Assert.Contains("blocked/7", failure.Message);
        Assert.Contains(_path, failure.Message);
        Assert.Contains("Close other VibeRails instances", failure.Message);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(10));
        Assert.Equal(5, waiting.DefaultTimeout);
        using var check = waiting.CreateCommand();
        check.CommandText = "PRAGMA busy_timeout;";
        Assert.Equal(5000L, check.ExecuteScalar());
        check.CommandText = "SELECT COUNT(*) FROM SchemaMigrations WHERE Component='blocked';";
        Assert.Equal(0L, check.ExecuteScalar());
        blocker.Rollback();
        Assert.True(SqliteMigrationRunner.Apply(waiting, "blocked", 7,
            (db, tx) => Execute(db, tx, "CREATE TABLE AfterRelease(Id INTEGER);")));
    }

    private static void Execute(SqliteConnection db, SqliteTransaction transaction, string sql)
    {
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        foreach (var path in new[] { _path, _path + "-wal", _path + "-shm" })
            File.Delete(path);
    }
}
