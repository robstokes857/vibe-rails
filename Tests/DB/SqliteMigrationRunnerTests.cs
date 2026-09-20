using Microsoft.Data.Sqlite;
using VibeRails.Data.Sqlite;
using VibeRails.Data.Abstractions;
using Xunit;

namespace Tests.DB;

public sealed class SqliteMigrationRunnerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"viberails-migrations-{Guid.NewGuid():N}");
    private string _path => Path.Combine(_root, "state.db");
    private string ConnectionString => new SqliteConnectionStringBuilder { DataSource = _path, Pooling = false }.ToString();

    public SqliteMigrationRunnerTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void FailedMigrationRollsBackSchemaAndCompletionRecord()
    {
        using var connection = SqliteConnectionFactory.Open(ConnectionString);
        Assert.Throws<InvalidOperationException>(() => SqliteMigrationRunner.Apply(connection, "test", 1, MigrationKind.Additive, (db, tx) =>
        {
            Execute(db, tx, "CREATE TABLE Example(Id INTEGER);");
            throw new InvalidOperationException("simulated interruption");
        }));
        using var check = connection.CreateCommand();
        check.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE name='Example';";
        Assert.Equal(0L, check.ExecuteScalar());
        Assert.True(SqliteMigrationRunner.Apply(connection, "test", 1, MigrationKind.Additive,
            (db, tx) => Execute(db, tx, "CREATE TABLE Example(Id INTEGER);")));
        Assert.False(SqliteMigrationRunner.Apply(connection, "test", 1, MigrationKind.Additive,
            (_, _) => throw new InvalidOperationException("already applied")));
    }

    [Fact]
    public void FailedSecondMigrationPreservesCommittedFirstVersionAndRetriesCleanly()
    {
        using var connection = SqliteConnectionFactory.Open(ConnectionString);
        Assert.True(SqliteMigrationRunner.Apply(connection, "upgrade", 1, MigrationKind.Additive, (db, tx) =>
            Execute(db, tx, "CREATE TABLE Preserved(Id INTEGER PRIMARY KEY, Value TEXT); INSERT INTO Preserved VALUES(1,'original');")));

        Assert.Throws<InvalidOperationException>(() => SqliteMigrationRunner.Apply(connection, "upgrade", 2, MigrationKind.Additive, (db, tx) =>
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
        Assert.False(SqliteMigrationRunner.Apply(connection, "upgrade", 1, MigrationKind.Additive, (_, _) => Assert.Fail("reran version 1")));

        Assert.True(SqliteMigrationRunner.Apply(connection, "upgrade", 2, MigrationKind.Additive, (db, tx) =>
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
            SqliteMigrationRunner.Apply(connection, "concurrent", 1, MigrationKind.Additive, (db, tx) =>
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
        Assert.False(SqliteMigrationRunner.Apply(reopened, "concurrent", 1, MigrationKind.Additive, (_, _) => Assert.Fail("reran")));
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
        SqliteMigrationRunner.Apply(writer, "seed", 1, MigrationKind.Additive, (db, tx) => Execute(db, tx, "CREATE TABLE Seed(Id INTEGER);"));
        using var blocker = writer.BeginTransaction(deferred: false);
        var applied = 0;
        var started = 0;
        var allStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workers = Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
        {
            using var connection = SqliteConnectionFactory.Open(ConnectionString);
            if (Interlocked.Increment(ref started) == 2) allStarted.SetResult();
            var result = SqliteMigrationRunner.Apply(connection, "contended", 1, MigrationKind.Additive, (db, tx) =>
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
        SqliteMigrationRunner.Apply(writer, "seed", 1, MigrationKind.Additive, (db, tx) => Execute(db, tx, "CREATE TABLE Seed(Id INTEGER);"));
        using var blocker = writer.BeginTransaction(deferred: false);
        using var waiting = SqliteConnectionFactory.Open(ConnectionString);
        var called = false;
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var failure = Assert.Throws<StorageException>(() => SqliteMigrationRunner.Apply(
            waiting, "blocked", 7, MigrationKind.Additive, (_, _) => called = true, lockTimeoutSeconds: 1));
        elapsed.Stop();
        Assert.False(called);
        Assert.True(failure.IsTransient);
        Assert.Contains("blocked/7", failure.Message);
        Assert.Contains(_path, failure.Message);
        Assert.Contains("upgrade will retry", failure.Message);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(10));
        Assert.Equal(5, waiting.DefaultTimeout);
        using var check = waiting.CreateCommand();
        check.CommandText = "PRAGMA busy_timeout;";
        Assert.Equal(5000L, check.ExecuteScalar());
        check.CommandText = "SELECT COUNT(*) FROM SchemaMigrations WHERE Component='blocked';";
        Assert.Equal(0L, check.ExecuteScalar());
        blocker.Rollback();
        Assert.True(SqliteMigrationRunner.Apply(waiting, "blocked", 7, MigrationKind.Additive,
            (db, tx) => Execute(db, tx, "CREATE TABLE AfterRelease(Id INTEGER);")));
    }

    [Fact]
    public void FreshDatabaseMigratesAutomaticallyWithoutABackup()
    {
        using var policy = SchemaUpgradePolicy.Scope(backup: true);
        using var connection = SqliteConnectionFactory.Open(ConnectionString);
        Assert.True(SqliteMigrationRunner.Apply(connection, "fresh", 1, MigrationKind.Breaking,
            (db, tx) => Execute(db, tx, "CREATE TABLE Fresh(Id INTEGER);")));
        Assert.Empty(BackupFiles());
    }

    [Fact]
    public void ExistingDatabaseMigratesAutomaticallyWhileAnotherConnectionRemainsOpen()
    {
        CreateExistingDatabase();
        using var connection = SqliteConnectionFactory.Open(ConnectionString);
        using var other = SqliteConnectionFactory.Open(ConnectionString);
        Assert.True(SqliteMigrationRunner.Apply(connection, "legacy", 2, MigrationKind.Breaking,
            (db, tx) => Execute(db, tx, "DROP TABLE Legacy;")));
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM SchemaMigrations WHERE Component='legacy' AND Version=2;"));
        Assert.Equal(0L, Scalar(other, "SELECT COUNT(*) FROM sqlite_schema WHERE name='Legacy';"));
    }

    [Fact]
    public void BreakingMigrationBacksUpTheDatabaseBeforeChangingIt()
    {
        CreateExistingDatabase();
        using var connection = SqliteConnectionFactory.Open(ConnectionString);
        using var policy = SchemaUpgradePolicy.Scope(backup: true);
        Assert.True(SqliteMigrationRunner.Apply(connection, "legacy", 1, MigrationKind.Breaking,
            (db, tx) => Execute(db, tx, "DROP TABLE Legacy;")));

        var backup = Assert.Single(BackupFiles());
        Assert.Contains("before-legacy-1", backup);
        using var copy = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = backup, Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        copy.Open();
        // The copy is the rollback: it still holds the table the migration removed.
        Assert.Equal(1L, Scalar(copy, "SELECT COUNT(*) FROM sqlite_schema WHERE name='Legacy';"));
        Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM sqlite_schema WHERE name='Legacy';"));
    }

    [Fact]
    public async Task ConcurrentAutomaticUpgradesWaitForWriter_BackUpLatestCommit_AndApplyOnce()
    {
        CreateExistingDatabase();
        using var policy = SchemaUpgradePolicy.Scope(backup: true);
        using var writer = SqliteConnectionFactory.Open(ConnectionString);
        SqliteConnectionFactory.EnsureWalMode(writer);
        using var blocker = writer.BeginTransaction(deferred: false);
        Execute(writer, blocker, "INSERT INTO Legacy VALUES(42);");
        var applied = 0;
        using var started = new CountdownEvent(2);
        var workers = Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
        {
            using var connection = SqliteConnectionFactory.Open(ConnectionString);
            started.Signal();
            return SqliteMigrationRunner.Apply(connection, "concurrent", 1, MigrationKind.Breaking, (db, tx) =>
            {
                Execute(db, tx, "DROP TABLE Legacy;");
                Interlocked.Increment(ref applied);
            });
        })).ToArray();
        try
        {
            Assert.True(started.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            await Task.Delay(250, TestContext.Current.CancellationToken);
            Assert.All(workers, worker => Assert.False(worker.IsCompleted));
            Assert.Empty(BackupFiles());
        }
        finally
        {
            blocker.Commit();
        }

        Assert.Single(await Task.WhenAll(workers), result => result);
        Assert.Equal(1, applied);
        using var copy = SqliteConnectionFactory.Open($"Data Source={Assert.Single(BackupFiles())};Pooling=False", readOnly: true);
        Assert.Equal(42L, Scalar(copy, "SELECT Id FROM Legacy;"));
        Assert.Equal(1L, Scalar(writer, "SELECT COUNT(*) FROM SchemaMigrations WHERE Component='concurrent';"));
    }

    [Fact]
    public void BackupFailureLeavesSchemaAndLedgerUnchanged()
    {
        CreateExistingDatabase();
        using var policy = SchemaUpgradePolicy.Scope(backup: true);
        File.WriteAllText(Path.Combine(_root, SqliteDatabaseBackup.BackupDirectoryName), "blocks the backup directory");
        using var connection = SqliteConnectionFactory.Open(ConnectionString);
        Assert.Throws<IOException>(() => SqliteMigrationRunner.Apply(connection, "legacy", 1, MigrationKind.Breaking,
            (db, tx) => Execute(db, tx, "DROP TABLE Legacy;")));
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM sqlite_schema WHERE name='Legacy';"));
        Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM sqlite_schema WHERE name='SchemaMigrations';"));
    }

    [Fact]
    public void FailedAutomaticUpgradeRollsBackAndRetriesWithoutOverwritingBackup()
    {
        CreateExistingDatabase();
        using var policy = SchemaUpgradePolicy.Scope(backup: true);
        using var connection = SqliteConnectionFactory.Open(ConnectionString);
        Assert.Throws<InvalidOperationException>(() => SqliteMigrationRunner.Apply(connection, "legacy", 1, MigrationKind.Breaking,
            (db, tx) =>
            {
                Execute(db, tx, "DROP TABLE Legacy;");
                throw new InvalidOperationException("interrupted upgrade");
            }));
        var firstBackup = Assert.Single(BackupFiles());
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM sqlite_schema WHERE name='Legacy';"));
        Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM sqlite_schema WHERE name='SchemaMigrations';"));
        Assert.True(SqliteMigrationRunner.Apply(connection, "legacy", 1, MigrationKind.Breaking,
            (db, tx) => Execute(db, tx, "DROP TABLE Legacy;")));
        Assert.Equal(2, BackupFiles().Count());
        Assert.Contains(firstBackup, BackupFiles());
    }

    [Fact]
    public void ReceiptsRecordWhichBinaryAppliedThem()
    {
        using var connection = SqliteConnectionFactory.Open(ConnectionString);
        Assert.Empty(SqliteMigrationRunner.ReadReceipts(connection));
        SqliteMigrationRunner.Apply(connection, "stamped", 1, MigrationKind.Additive,
            (db, tx) => Execute(db, tx, "CREATE TABLE Stamped(Id INTEGER);"));
        var receipt = Assert.Single(SqliteMigrationRunner.ReadReceipts(connection));
        Assert.Equal("stamped", receipt.Component);
        Assert.Equal(1, receipt.Version);
        Assert.False(string.IsNullOrWhiteSpace(receipt.AppliedBy));
        Assert.Contains("pid=" + Environment.ProcessId, receipt.AppliedBy);
    }

    [Fact]
    public void GenerationGateRefusesNewerDatabasesAndTheStampNeverLowers()
    {
        using var connection = SqliteConnectionFactory.Open(ConnectionString);
        Assert.Equal(0L, SqliteMigrationRunner.ReadGeneration(connection));

        SqliteMigrationRunner.StampGeneration(connection, 2);
        Assert.Equal(2L, SqliteMigrationRunner.ReadGeneration(connection));
        SqliteMigrationRunner.StampGeneration(connection, 1);
        Assert.Equal(2L, SqliteMigrationRunner.ReadGeneration(connection));

        // Same generation and a newer build both open it; an older build must not.
        SqliteMigrationRunner.RequireGenerationAtMost(connection, 2, "test");
        SqliteMigrationRunner.RequireGenerationAtMost(connection, 3, "test");
        var refused = Assert.Throws<StorageException>(() => SqliteMigrationRunner.RequireGenerationAtMost(connection, 1, "test"));
        Assert.False(refused.IsTransient);
        Assert.Contains("newer VibeRails", refused.Message);
        Assert.Contains("generation 2", refused.Message);
        Assert.Contains("generation 1", refused.Message);
        Assert.Contains(_path, refused.Message);
    }

    private void CreateExistingDatabase()
    {
        // Written outside the runner, before its first contact with the file: what an older build
        // leaves behind looks exactly like this.
        using var raw = new SqliteConnection(ConnectionString);
        raw.Open();
        using var command = raw.CreateCommand();
        command.CommandText = "CREATE TABLE Legacy(Id INTEGER);";
        command.ExecuteNonQuery();
    }

    private IEnumerable<string> BackupFiles()
    {
        var directory = Path.Combine(Path.GetDirectoryName(_path)!, SqliteDatabaseBackup.BackupDirectoryName);
        return Directory.Exists(directory)
            ? Directory.GetFiles(directory, Path.GetFileNameWithoutExtension(_path) + ".*")
            : [];
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
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
        Directory.Delete(_root, recursive: true);
    }
}
