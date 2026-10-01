using Microsoft.Data.Sqlite;
using VibeRails.Data.Sqlite;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services;
using VibeRails.Services.Board;
using Xunit;

namespace Tests.Services.Board;

/// <summary>
/// VIBE-27: the Board store's reads into state.db probe the schema first so a stdio host can
/// open a file that never held Automations. Those probes sit on the activity poll and every
/// get_board_card, so a positive answer is remembered per store instance. What must not regress:
/// a feature that is missing is still noticed the moment another process creates it.
/// </summary>
public sealed class BoardStoreSchemaProbeCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"viberails-board-probe-{Guid.NewGuid():N}");
    private readonly string _stateConnectionString;
    private readonly string _boardConnectionString;

    public BoardStoreSchemaProbeCacheTests()
    {
        Directory.CreateDirectory(_root);
        _stateConnectionString = $"Data Source={Path.Combine(_root, "state.db")};Pooling=False";
        _boardConnectionString = $"Data Source={Path.Combine(_root, "board.db")};Pooling=False";
    }

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SchemaFeatures_RememberPresence_ButAskAgainWhileAbsent()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(Ct);
        await ExecuteAsync(connection, "CREATE TABLE Early (Id INTEGER PRIMARY KEY, Seen TEXT);");
        var features = new SqliteSchemaFeatures();

        Assert.True(features.HasColumn(connection, "Early", "Seen"));
        Assert.True(await features.HasTableAsync(connection, "Early", Ct));
        Assert.False(features.HasColumn(connection, "Early", "Later"));
        Assert.False(await features.HasTableAsync(connection, "Later", Ct));

        // A missing feature is re-probed every time, so it is noticed as soon as it exists.
        await ExecuteAsync(connection, "ALTER TABLE Early ADD COLUMN Later TEXT; CREATE TABLE Later (Id INTEGER PRIMARY KEY);");
        Assert.True(features.HasColumn(connection, "Early", "Later"));
        Assert.True(await features.HasTableAsync(connection, "Later", Ct));

        // Presence is remembered: the schema policy never drops a table or column, so the
        // memo is never consulted against the file again for the life of the instance.
        await ExecuteAsync(connection, "ALTER TABLE Early DROP COLUMN Seen; DROP TABLE Later;");
        Assert.True(features.HasColumn(connection, "Early", "Seen"));
        Assert.True(await features.HasTableAsync(connection, "Later", Ct));
        Assert.False(new SqliteSchemaFeatures().HasColumn(connection, "Early", "Seen"));
    }

    [Fact]
    public async Task LaneAutomationDescriptions_NoticeAutomationsTablesCreatedAfterTheFirstRead()
    {
        StateDatabaseSchema.Ensure(_stateConnectionString);
        var store = new BoardStore(_boardConnectionString, _stateConnectionString);

        // A fresh stdio host: state.db has terminal history but has never held Automations.
        var unavailable = Assert.Single(await store.DescribeLaneAutomationsAsync(_root, [1], Ct));
        Assert.Null(unavailable.Name);
        Assert.Empty(await store.GetRunningAutomationsAsync(_root, Ct));
        Assert.Empty(await store.GetAutomationSessionIdsAsync(_root, ["session-1"], Ct));

        // A root initialises Jobs in another process; the same store instance must see it.
        var jobs = new JobStore(_stateConnectionString);
        var job = await jobs.CreateJobAsync(new("review", _root, LLM.NotSet, null, "", null, true, [],
            Actions: [new(null, JobActionKind.Script, ScriptPath: "check.py", ScriptRuntime: JobScriptRuntime.Python)],
            Description: "Reviews the branch."), Ct);
        var described = Assert.Single(await store.DescribeLaneAutomationsAsync(_root, [job.Id], Ct));
        Assert.Equal("review", described.Name);
        Assert.Equal("Reviews the branch.", described.Description);
        Assert.Empty(await store.GetRunningAutomationsAsync(_root, Ct));
    }

    [Fact]
    public async Task SessionLookups_StillReadRowsFresh_OnlyTheSchemaProbeIsRemembered()
    {
        StateDatabaseSchema.Ensure(_stateConnectionString);
        var store = new BoardStore(_boardConnectionString, _stateConnectionString);
        Assert.Null(await store.FindSessionAuthorAsync("session-1", Ct));
        Assert.Null(await store.FindSessionOutcomeAsync("session-1", Ct));

        await InsertSessionAsync("session-1", "claude", "Review worker");
        var author = await store.FindSessionAuthorAsync("session-1", Ct);
        Assert.NotNull(author);
        Assert.Equal("Review worker", author.Label);
        Assert.Equal(LlmParser.ToWireName(LLM.Claude), author.Cli);
        Assert.Equal("session-1", author.SessionId);
        Assert.NotNull(await store.FindSessionOutcomeAsync("session-1", Ct));

        // Only "the Sessions table exists" is remembered. Row reads stay live, so pruned
        // history is noticed on the same instance (BoardDatabaseIsolationTests pins the
        // card-session fallback that takes over from there).
        await ExecuteAsync("DELETE FROM Sessions WHERE Id = 'session-1';");
        Assert.Null(await store.FindSessionAuthorAsync("session-1", Ct));
        Assert.Null(await store.FindSessionOutcomeAsync("session-1", Ct));
    }

    private async Task InsertSessionAsync(string id, string cli, string? environment)
    {
        await using var connection = new SqliteConnection(_stateConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Sessions (Id, Cli, EnvironmentName, WorkingDirectory, StartedUTC)
            VALUES ($id, $cli, $environment, $directory, $started);
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$cli", cli);
        command.Parameters.AddWithValue("$environment", (object?)environment ?? DBNull.Value);
        command.Parameters.AddWithValue("$directory", _root);
        command.Parameters.AddWithValue("$started", DateTime.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(Ct);
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new SqliteConnection(_stateConnectionString);
        await connection.OpenAsync(Ct);
        await ExecuteAsync(connection, sql);
    }

    private async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(Ct);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
