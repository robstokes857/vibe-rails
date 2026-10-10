using Microsoft.Data.Sqlite;
using VibeRails.Data.Sqlite;
using VibeRails.Data.Sqlite.Replay;
using Xunit;

namespace Tests.DB;

public sealed class ReplayTokenSavingsTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "replay-savings-" + Guid.NewGuid().ToString("N"));
    private readonly string state;
    private readonly string proxy;
    private readonly ReplayStore store;

    public ReplayTokenSavingsTests()
    {
        Directory.CreateDirectory(directory);
        state = Path.Combine(directory, "state.db");
        proxy = Path.Combine(directory, "proxy.db");
        Execute(state, """
            CREATE TABLE Sessions(Id TEXT, Cli TEXT, EnvironmentName TEXT, WorkingDirectory TEXT,
                ProjectDisplayName TEXT, SessionDisplayName TEXT, StartedUTC TEXT, EndedUTC TEXT, ExitCode INTEGER);
            CREATE TABLE SessionLogs(Id INTEGER, SessionId TEXT, Timestamp TEXT, Content BLOB);
            CREATE TABLE TerminalSessionLogs(Id INTEGER, SessionId TEXT, Sequence INTEGER, Timestamp TEXT, Cols INTEGER, Rows INTEGER, Data BLOB);
            CREATE TABLE UserInputs(Id INTEGER, SessionId TEXT, Sequence INTEGER, TimestampUTC TEXT, InputText TEXT);
            CREATE TABLE InputFileChanges(Id INTEGER, UserInputId INTEGER, PreviousInputId INTEGER, FilePath TEXT,
                ChangeType TEXT, LinesAdded INTEGER, LinesDeleted INTEGER, DiffContent TEXT);
            INSERT INTO Sessions VALUES('one','codex','','','','Recording','2026-10-10T00:00:00Z',NULL,NULL);
            """);
        store = new ReplayStore(new SqliteStoragePaths(state, null, proxy));
    }

    private void CreateProxy() => Execute(proxy, """
        CREATE TABLE ProxyExchanges(SessionId TEXT, CreatedUTC TEXT, CharsBefore INTEGER, CharsAfter INTEGER);
        """);

    [Fact]
    public void SavingsUseExactSessionAndNetReduction_RoundedOnceAtTheSnapshot()
    {
        CreateProxy();
        Execute(proxy, """
            INSERT INTO ProxyExchanges VALUES
                ('one','2026-10-10T00:01:00Z',10,3),
                ('one','2026-10-10T00:02:00Z',10,3),
                ('one','2026-10-10T00:03:00Z',2,4),
                ('other','2026-10-10T00:04:00Z',9999999,0),
                (NULL,'2026-10-10T00:05:00Z',9999999,0);
            """);
        var stateBefore = File.ReadAllBytes(state);
        var proxyBefore = File.ReadAllBytes(proxy);
        var snapshot = store.Manifest("one")!;
        Assert.Equal(3, snapshot.TokensSaved);
        Assert.Equal(3, snapshot.ProxyMaxId);
        Assert.Equal(stateBefore, File.ReadAllBytes(state));
        Assert.Equal(proxyBefore, File.ReadAllBytes(proxy));

        Execute(proxy, "INSERT INTO ProxyExchanges VALUES('one','2026-10-10T00:06:00Z',400,0)");
        Assert.Equal(3, snapshot.TokensSaved);
        var reloaded = store.Manifest("one")!;
        Assert.Equal(103, reloaded.TokensSaved);
        Assert.Equal(6, reloaded.ProxyMaxId);
    }

    [Fact]
    public void MissingCapturesStayUnavailable_WithoutCreatingAProxyDatabase()
    {
        Assert.Null(store.Manifest("one")!.TokensSaved);
        Assert.False(File.Exists(proxy));
        CreateProxy();
        Assert.Null(store.Manifest("one")!.TokensSaved);
        Assert.Null(store.Manifest("missing"));
    }

    [Theory]
    [InlineData(10L, 10L, 0L)]
    [InlineData(10L, 20L, 0L)]
    [InlineData(12000000000L, 0L, 3000000000L)]
    public void ZeroExpansionAndLargeTotals(long before, long after, long expected)
    {
        CreateProxy();
        using var db = new SqliteConnection($"Data Source={proxy};Pooling=False");
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "INSERT INTO ProxyExchanges VALUES('one','2026-10-10T00:01:00Z',$before,$after)";
        command.Parameters.AddWithValue("$before", before);
        command.Parameters.AddWithValue("$after", after);
        command.ExecuteNonQuery();
        Assert.Equal(expected, store.Manifest("one")!.TokensSaved);
    }

    private static void Execute(string path, string sql)
    {
        using var db = new SqliteConnection($"Data Source={path};Pooling=False");
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);
}
