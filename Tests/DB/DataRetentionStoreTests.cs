using Microsoft.Data.Sqlite;
using VibeRails.DB;
using VibeRails.Data.Sqlite;
using Xunit;

namespace Tests.DB;

public sealed class DataRetentionStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"viberails-retention-{Guid.NewGuid():N}");
    private readonly DateTime _now = new(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc);
    private string State => new SqliteConnectionStringBuilder { DataSource = Path.Combine(_directory, "state.db"), Pooling = false }.ToString();
    private string Proxy => Path.Combine(_directory, "proxy.db");

    [Fact]
    public async Task DeletesOnlyOldAcknowledgedCompletedDataAndPreservesNewerReferences()
    {
        Directory.CreateDirectory(_directory);
        var repository = new Repository(State);
        var expired = await Session(repository, "expired", _now.AddMonths(-2), exported: true);
        var unexported = await Session(repository, "unexported", _now.AddMonths(-2), exported: false);
        var recent = await Session(repository, "recent", _now.AddDays(-2), exported: true);
        var recentProxy = await Session(repository, "recent-proxy", _now.AddMonths(-2), exported: true);
        var open = await Session(repository, "open", _now.AddMonths(-2), exported: true, ended: false);
        var oldInput = await repository.InsertUserInputAsync(expired, 1, "old input", null);
        var newInput = await repository.InsertUserInputAsync(recent, 1, "recent input", null);
        using (var connection = SqliteConnectionFactory.Open(State))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO InputFileChanges(UserInputId,PreviousInputId,FilePath,ChangeType) VALUES ($new,$old,'kept.txt','modified');";
            command.Parameters.AddWithValue("$new", newInput);
            command.Parameters.AddWithValue("$old", oldInput);
            command.ExecuteNonQuery();
        }
        using (var proxy = SqliteConnectionFactory.Open(new SqliteConnectionStringBuilder { DataSource = Proxy, Pooling = false }.ToString()))
        {
            using var create = proxy.CreateCommand();
            create.CommandText = "CREATE TABLE ProxyExchanges(Id TEXT PRIMARY KEY,SessionId TEXT,CreatedUTC TEXT);";
            create.ExecuteNonQuery();
            foreach (var (id, session, time) in new[]
            {
                ("old", expired, _now.AddDays(-10)), ("not-uploaded", unexported, _now.AddDays(-10)),
                ("still-open", open, _now.AddDays(-10)), ("recent", recentProxy, _now.AddDays(-2)),
                ("unknown", (string?)null, _now.AddDays(-10))
            })
            {
                using var insert = proxy.CreateCommand();
                insert.CommandText = "INSERT INTO ProxyExchanges VALUES ($id,$session,$utc);";
                insert.Parameters.AddWithValue("$id", id);
                insert.Parameters.AddWithValue("$session", (object?)session ?? DBNull.Value);
                insert.Parameters.AddWithValue("$utc", time.ToString("O"));
                insert.ExecuteNonQuery();
            }
        }

        var store = new SqliteDataRetentionStore(State, Proxy);
        var result = await store.PruneAsync(_now, TestContext.Current.CancellationToken);
        Assert.Equal(1, result.SessionsDeleted);
        Assert.Equal(1, result.ProxyExchangesDeleted);
        Assert.Null(await repository.GetSessionByIdAsync(expired, TestContext.Current.CancellationToken));
        foreach (var id in new[] { unexported, recent, recentProxy, open })
            Assert.NotNull(await repository.GetSessionByIdAsync(id, TestContext.Current.CancellationToken));
        using var check = SqliteConnectionFactory.Open(State);
        using var query = check.CreateCommand();
        query.CommandText = "SELECT COUNT(*) FROM InputFileChanges WHERE UserInputId=$id AND PreviousInputId IS NULL;";
        query.Parameters.AddWithValue("$id", newInput);
        Assert.Equal(1L, query.ExecuteScalar());
        query.CommandText = "PRAGMA foreign_key_check;";
        Assert.Null(query.ExecuteScalar());
        var again = await store.PruneAsync(_now, TestContext.Current.CancellationToken);
        Assert.Equal(0, again.SessionsDeleted);
        Assert.Equal(0, again.ProxyExchangesDeleted);
    }

    [Fact]
    public async Task LegacyCleanedInputReferencesAreUnlinkedBeforeDeletionWithoutLosingSurvivingPrompts()
    {
        Directory.CreateDirectory(_directory);
        var repository = new Repository(State);
        var expired = await Session(repository, "expired-cleaned", _now.AddMonths(-2), exported: true);
        var recent = await Session(repository, "recent-cleaned", _now.AddDays(-2), exported: true);
        var oldInput = await repository.InsertUserInputAsync(expired, 1, "old original prompt", null);
        var recentInput = await repository.InsertUserInputAsync(recent, 1, "keep original prompt", null);
        using (var connection = SqliteConnectionFactory.Open(State))
        using (var seed = connection.CreateCommand())
        {
            seed.CommandText = """
                CREATE TABLE CleanedUserInput (
                    Id INTEGER PRIMARY KEY, SessionId TEXT NOT NULL REFERENCES Sessions(Id),
                    UserInputId INTEGER NOT NULL REFERENCES UserInputs(Id) ON DELETE CASCADE,
                    CleanedText TEXT NOT NULL);
                ALTER TABLE UserInputs ADD COLUMN CleanedId INTEGER REFERENCES CleanedUserInput(Id);
                CREATE INDEX idx_user_inputs_cleaned_id ON UserInputs(CleanedId);
                INSERT INTO CleanedUserInput VALUES(1,$oldSession,$oldInput,'old derived text');
                INSERT INTO CleanedUserInput VALUES(2,$newSession,$newInput,'keep derived text');
                UPDATE UserInputs SET CleanedId=1 WHERE Id=$oldInput;
                UPDATE UserInputs SET CleanedId=2 WHERE Id=$newInput;
                INSERT INTO UserInputs(SessionId,Sequence,InputText,TimestampUTC,CleanedId)
                    VALUES($newSession,2,'surviving cross-reference','2026-09-14',1);
                """;
            seed.Parameters.AddWithValue("$oldSession", expired);
            seed.Parameters.AddWithValue("$newSession", recent);
            seed.Parameters.AddWithValue("$oldInput", oldInput);
            seed.Parameters.AddWithValue("$newInput", recentInput);
            seed.ExecuteNonQuery();
        }

        var result = await new SqliteDataRetentionStore(State, Proxy).PruneAsync(_now, TestContext.Current.CancellationToken);
        Assert.Equal(1, result.SessionsDeleted);
        Assert.Null(await repository.GetSessionByIdAsync(expired, TestContext.Current.CancellationToken));
        using var check = SqliteConnectionFactory.Open(State);
        using var query = check.CreateCommand();
        query.CommandText = "SELECT COUNT(*) FROM UserInputs WHERE InputText='surviving cross-reference' AND CleanedId IS NULL;";
        Assert.Equal(1L, query.ExecuteScalar());
        query.CommandText = "SELECT COUNT(*) FROM UserInputs WHERE InputText='keep original prompt' AND CleanedId=2;";
        Assert.Equal(1L, query.ExecuteScalar());
        query.CommandText = "SELECT CleanedText FROM CleanedUserInput;";
        Assert.Equal("keep derived text", query.ExecuteScalar());
        query.CommandText = "PRAGMA foreign_key_check;";
        Assert.Null(query.ExecuteScalar());
    }

    [Fact]
    public async Task LargeSessionResumesAcrossBoundedBatches()
    {
        Directory.CreateDirectory(_directory);
        var repository = new Repository(State);
        var session = await Session(repository, "large", _now.AddMonths(-2), exported: true);
        using (var state = SqliteConnectionFactory.Open(State))
        {
            using var command = state.CreateCommand();
            command.CommandText = """
                WITH RECURSIVE n(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM n WHERE x < $count)
                INSERT INTO SessionLogs(SessionId,Timestamp,Content,IsError) SELECT $session,'2026-07-01',X'00',0 FROM n;
                """;
            command.Parameters.AddWithValue("$count", SqliteDataRetentionStore.StateBatchSize * SqliteDataRetentionStore.MaxStateBatches + 1);
            command.Parameters.AddWithValue("$session", session);
            command.ExecuteNonQuery();
        }
        var store = new SqliteDataRetentionStore(State, Proxy);
        var first = await store.PruneAsync(_now, TestContext.Current.CancellationToken);
        Assert.Equal(0, first.SessionsDeleted);
        Assert.Equal(SqliteDataRetentionStore.StateBatchSize * SqliteDataRetentionStore.MaxStateBatches, first.StateRowsDeleted);
        var second = await store.PruneAsync(_now, TestContext.Current.CancellationToken);
        Assert.Equal(1, second.SessionsDeleted);
        Assert.Equal(1, second.StateRowsDeleted);
    }

    [Theory]
    // Only an "included" snapshot is proof a row was uploaded, and only for rows up to its largest
    // rowid (the helper models a snapshot taken after this row was written).
    [InlineData("included", 0)]
    // An "empty" snapshot contained nothing, so a row present now arrived after it.
    [InlineData("empty", 1)]
    // A v2 envelope whose proxy read failed is still acknowledged and still sets ExportedUTC.
    [InlineData("unavailable", 1)]
    // A historical or frozen schema-v1 acknowledgement carries no proxy data at all.
    [InlineData(null, 1)]
    public async Task ProxyRowsSurviveUnlessTheAcknowledgedEnvelopeProvesTheyWereBackedUp(
        string? coverage, int expectedSurvivors)
    {
        Directory.CreateDirectory(_directory);
        var repository = new Repository(State);
        var session = await Session(repository, "acknowledged", _now.AddMonths(-2),
            exported: true, proxyCoverage: coverage);
        using (var proxy = SqliteConnectionFactory.Open(
            new SqliteConnectionStringBuilder { DataSource = Proxy, Pooling = false }.ToString()))
        {
            using var create = proxy.CreateCommand();
            create.CommandText = "CREATE TABLE ProxyExchanges(Id TEXT PRIMARY KEY,SessionId TEXT,CreatedUTC TEXT);";
            create.ExecuteNonQuery();
            create.CommandText = "INSERT INTO ProxyExchanges VALUES ('ex',$session,$utc);";
            create.Parameters.AddWithValue("$session", session);
            create.Parameters.AddWithValue("$utc", _now.AddDays(-10).ToString("O"));
            create.ExecuteNonQuery();
        }

        var store = new SqliteDataRetentionStore(State, Proxy);
        await store.PruneAsync(_now, TestContext.Current.CancellationToken);

        using var check = SqliteConnectionFactory.Open(
            new SqliteConnectionStringBuilder { DataSource = Proxy, Pooling = false }.ToString());
        using var count = check.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM ProxyExchanges;";
        Assert.Equal((long)expectedSurvivors, count.ExecuteScalar());
    }

    [Fact]
    public async Task IncludedSnapshotOnlyCoversRowsUpToItsHighWaterMark()
    {
        Directory.CreateDirectory(_directory);
        var repository = new Repository(State);
        var session = Guid.NewGuid().ToString("D");
        await repository.CreateSessionAsync(session, "test", "late-write", _directory, Environment.ProcessId);
        await repository.CompleteSessionAsync(session, 0);
        using (var state = SqliteConnectionFactory.Open(State))
        {
            using var age = state.CreateCommand();
            age.CommandText = "UPDATE Sessions SET EndedUTC=$ended WHERE Id=$id;";
            age.Parameters.AddWithValue("$ended", _now.AddMonths(-2).ToString("O"));
            age.Parameters.AddWithValue("$id", session);
            age.ExecuteNonQuery();
        }
        using (var proxy = SqliteConnectionFactory.Open(new SqliteConnectionStringBuilder { DataSource = Proxy, Pooling = false }.ToString()))
        {
            using var create = proxy.CreateCommand();
            create.CommandText = "CREATE TABLE ProxyExchanges(Id TEXT PRIMARY KEY,SessionId TEXT,CreatedUTC TEXT);";
            create.ExecuteNonQuery();
            create.CommandText = "INSERT INTO ProxyExchanges VALUES ('uploaded',$session,$utc);";
            create.Parameters.AddWithValue("$session", session);
            create.Parameters.AddWithValue("$utc", _now.AddDays(-10).ToString("O"));
            create.ExecuteNonQuery();
        }
        // The acknowledged envelope's snapshot ended at rowid 1. The proxy then writes a queued
        // exchange it had stamped before the snapshot, so by CreatedUTC alone it looks uploaded.
        Assert.True(await repository.MarkSessionExportedAsync(
            session, _now.AddMonths(-2), "included", 1, TestContext.Current.CancellationToken));
        using (var proxy = SqliteConnectionFactory.Open(new SqliteConnectionStringBuilder { DataSource = Proxy, Pooling = false }.ToString()))
        {
            using var late = proxy.CreateCommand();
            late.CommandText = "INSERT INTO ProxyExchanges VALUES ('written-after-snapshot',$session,$utc);";
            late.Parameters.AddWithValue("$session", session);
            late.Parameters.AddWithValue("$utc", _now.AddDays(-10).ToString("O"));
            late.ExecuteNonQuery();
        }

        var result = await new SqliteDataRetentionStore(State, Proxy).PruneAsync(_now, TestContext.Current.CancellationToken);

        Assert.Equal(1, result.ProxyExchangesDeleted);
        using var check = SqliteConnectionFactory.Open(new SqliteConnectionStringBuilder { DataSource = Proxy, Pooling = false }.ToString());
        using var remaining = check.CreateCommand();
        remaining.CommandText = "SELECT Id FROM ProxyExchanges;";
        Assert.Equal("written-after-snapshot", remaining.ExecuteScalar());
        var again = await new SqliteDataRetentionStore(State, Proxy).PruneAsync(_now, TestContext.Current.CancellationToken);
        Assert.Equal(0, again.ProxyExchangesDeleted);
    }

    /// <param name="proxyCoverage">
    /// The acknowledged envelope's proxy coverage. "included" is the ordinary v2 result; null
    /// models a historical or v1 acknowledgement, which is NOT proof the proxy rows were backed up.
    /// </param>
    /// <param name="proxyMaxRowId">
    /// The largest proxy rowid the acknowledged "included" snapshot contained. The default models
    /// a snapshot taken after every proxy row the test writes.
    /// </param>
    private async Task<string> Session(Repository repository, string name, DateTime time, bool exported,
        bool ended = true, string? proxyCoverage = "included", long proxyMaxRowId = long.MaxValue)
    {
        var id = Guid.NewGuid().ToString("D");
        await repository.CreateSessionAsync(id, "test", name, _directory, Environment.ProcessId);
        using var connection = SqliteConnectionFactory.Open(State);
        using var update = connection.CreateCommand();
        update.CommandText = "UPDATE Sessions SET StartedUTC=$start, EndedUTC=$end, ExportedUTC=$exported, ExportedProxyCoverage=$coverage, ExportedProxyMaxRowId=$maxRowId WHERE Id=$id;";
        update.Parameters.AddWithValue("$id", id);
        update.Parameters.AddWithValue("$start", time.ToString("O"));
        update.Parameters.AddWithValue("$end", ended ? time.ToString("O") : DBNull.Value);
        update.Parameters.AddWithValue("$exported", exported ? time.ToString("O") : DBNull.Value);
        update.Parameters.AddWithValue("$coverage", exported && proxyCoverage is not null ? proxyCoverage : (object)DBNull.Value);
        update.Parameters.AddWithValue("$maxRowId", exported && proxyCoverage == "included" ? proxyMaxRowId : (object)DBNull.Value);
        update.ExecuteNonQuery();
        return id;
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
