using Microsoft.Data.Sqlite;
using VibeRails.Data.Sqlite;
using VibeRails.DB;
using Xunit;

namespace Tests.DB;

public sealed class DbRefactorReviewReproTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "db-review-" + Guid.NewGuid().ToString("N"));
    private string State => new SqliteConnectionStringBuilder { DataSource = Path.Combine(_directory, "state.db"), Pooling = false }.ToString();

    public DbRefactorReviewReproTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task LegacyWriterAndNewMaintenanceMustPreserveFtsIntegrity()
    {
        var repository = new Repository(State);
        await repository.CreateSessionAsync("legacy", "test", null, ".", Environment.ProcessId);
        using var connection = SqliteConnectionFactory.Open(State);
        Execute(connection, """
            INSERT INTO UserInputs(SessionId,Sequence,InputText,TimestampUTC)
                VALUES('legacy',1,'searchable migration text','2026-09-16');
            INSERT INTO UserInputs_fts(rowid,InputText)
                VALUES(last_insert_rowid(),'searchable migration text');
            """);
        await new SqliteSearchIndexMaintenanceStore(State).RepairPendingAsync();
        Execute(connection, "INSERT INTO UserInputs_fts(UserInputs_fts,rank) VALUES('integrity-check',1);");
    }

    [Fact]
    public async Task ManualDeletionMustHandleLegacyCleanedInputReferences()
    {
        var repository = new Repository(State);
        await repository.CreateSessionAsync("legacy", "test", null, ".", Environment.ProcessId);
        await repository.InsertUserInputAsync("legacy", 1, "searchable original text", null);
        using var connection = SqliteConnectionFactory.Open(State);
        Execute(connection, """
            CREATE TABLE CleanedUserInput (
                Id INTEGER PRIMARY KEY, SessionId TEXT NOT NULL REFERENCES Sessions(Id),
                UserInputId INTEGER NOT NULL REFERENCES UserInputs(Id) ON DELETE CASCADE,
                CleanedText TEXT NOT NULL);
            ALTER TABLE UserInputs ADD COLUMN CleanedId INTEGER REFERENCES CleanedUserInput(Id);
            INSERT INTO CleanedUserInput SELECT 1,'legacy',Id,'derived' FROM UserInputs;
            UPDATE UserInputs SET CleanedId=1;
            """);
        Assert.True(await repository.DeleteChatHistorySessionAsync("legacy", CancellationToken.None));
    }

    [Fact]
    public async Task ProxyExchangesNotPresentInAcknowledgedSnapshotMustSurviveRetention()
    {
        var now = new DateTime(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc);
        var repository = new Repository(State);
        await repository.CreateSessionAsync("late", "test", null, ".", Environment.ProcessId);
        await repository.CompleteSessionAsync("late", 0);
        using var state = SqliteConnectionFactory.Open(State);
        Execute(state, "UPDATE Sessions SET EndedUTC='2026-09-01T00:00:00.0000000Z' WHERE Id='late';");
        var proxyPath = Path.Combine(_directory, "proxy.db");
        using var proxy = SqliteConnectionFactory.Open(new SqliteConnectionStringBuilder { DataSource = proxyPath, Pooling = false }.ToString());
        Execute(proxy, "CREATE TABLE ProxyExchanges(Id TEXT PRIMARY KEY,SessionId TEXT,CreatedUTC TEXT);");
        // The acknowledged envelope saw an empty proxy snapshot. A queued write from
        // another process then arrives, with the time recorded when it was queued.
        await repository.MarkSessionExportedAsync("late", now.AddDays(-14), "empty", CancellationToken.None);
        Execute(proxy, "INSERT INTO ProxyExchanges VALUES('never-uploaded','late','2026-09-01T00:00:00.0000000Z');");
        var result = await new SqliteDataRetentionStore(State, proxyPath).PruneAsync(now, CancellationToken.None);
        Assert.Equal(0, result.ProxyExchangesDeleted);
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        foreach (var path in Directory.EnumerateFiles(_directory)) File.Delete(path);
        Directory.Delete(_directory);
    }
}
