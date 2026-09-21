using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using VibeRails.Data.Sqlite;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services;
using VibeRails.Services.Board;
using Xunit;

namespace Tests.DB;

public sealed class BoardDatabaseIsolationTests : IDisposable
{
    // Semicolons ensure composition uses a connection-string builder for both files.
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "board-split;" + Guid.NewGuid().ToString("N"));
    private string StatePath => Path.Combine(_directory, "state.db");
    private string BoardPath => Path.Combine(_directory, "board.db");
    private string StateConnectionString => new SqliteConnectionStringBuilder { DataSource = StatePath, Pooling = false }.ToString();
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public BoardDatabaseIsolationTests() => Directory.CreateDirectory(_directory);

    [Theory]
    [InlineData("missing")]
    [InlineData("newer")]
    [InlineData("damaged")]
    public async Task JobOnlyHosts_EnqueueCommitRunsWithoutOpeningBoardDatabase(string boardState)
    {
        if (boardState == "newer")
        {
            using var board = OpenBoard();
            Execute(board, "PRAGMA user_version=999;");
        }
        if (boardState == "damaged")
            await File.WriteAllTextAsync(BoardPath, "Not a SQLite database", Ct);
        var originalBoard = File.Exists(BoardPath) ? await File.ReadAllBytesAsync(BoardPath, Ct) : null;

        var direct = SqliteStorage.CreateJobStore(StatePath);
        using (var state = SqliteConnectionFactory.Open(StateConnectionString))
            Execute(state, SqlStrings.CreateEnvironmentsTable);
        var job = await direct.CreateJobAsync(new("Commit job", _directory, LLM.NotSet, null, "", null, true,
            [new(JobTriggerKind.Commit), new(JobTriggerKind.PreCommit)], Actions:
            [new(null, JobActionKind.Script, ScriptPath: "check.py", ScriptRuntime: JobScriptRuntime.Python, ApprovedHash: "pinned")]), Ct);

        var hookServices = new ServiceCollection();
        hookServices.AddSqliteJobStorage(_ => StatePath);
        Assert.DoesNotContain(hookServices, service => service.ServiceType == typeof(IBoardStore));
        using var hook = hookServices.BuildServiceProvider();
        var runtimeServices = new ServiceCollection();
        runtimeServices.AddSqliteStateStorage(_ => new SqliteStoragePaths(StatePath));
        using var runtime = runtimeServices.BuildServiceProvider();
        var index = 0;
        foreach (var store in new[] { direct, hook.GetRequiredService<IJobStore>(), runtime.GetRequiredService<IJobStore>() })
        {
            var kind = index == 1 ? JobTriggerKind.PreCommit : JobTriggerKind.Commit;
            var runId = Assert.Single(await store.EnqueueEventRunsAsync(_directory, kind, $"commit-{index++}", Ct));
            Assert.Equal(job.Id, (await store.GetRunAsync(runId, Ct))!.JobId);
            await store.CompleteRunAsync(runId, JobRunStatus.Succeeded, 0, null, Ct);
        }
        if (originalBoard is null)
            Assert.False(File.Exists(BoardPath));
        else
            Assert.Equal(originalBoard, await File.ReadAllBytesAsync(BoardPath, Ct));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SchedulerOptIn_ConsumesLaneEventsRegardlessOfStorageRegistrationOrder(bool schedulerFirst)
    {
        var services = new ServiceCollection();
        if (schedulerFirst)
            services.AddSqliteStateStorage(_ => new SqliteStoragePaths(StatePath), consumeBoardEvents: true);
        services.AddSqliteJobStorage(_ => StatePath);
        services.AddSqliteStateStorage(_ => new SqliteStoragePaths(StatePath));
        if (!schedulerFirst)
            services.AddSqliteStateStorage(_ => new SqliteStoragePaths(StatePath), consumeBoardEvents: true);
        using var provider = services.BuildServiceProvider();
        var jobs = provider.GetRequiredService<IJobStore>();
        using (var state = SqliteConnectionFactory.Open(StateConnectionString))
            Execute(state, SqlStrings.CreateEnvironmentsTable);
        var job = await jobs.CreateJobAsync(new("Lane job", _directory, LLM.NotSet, null, "", null, true, [], Actions:
            [new(null, JobActionKind.Script, ScriptPath: "check.py", ScriptRuntime: JobScriptRuntime.Python, ApprovedHash: "pinned")]), Ct);
        var boards = provider.GetRequiredService<IBoardStore>();
        var board = await boards.CreateBoardAsync(_directory, "Main", Ct);
        var lane = (await boards.GetColumnsAsync(_directory, Ct, board.Id))[0];
        await boards.SaveLaneAutomationAsync(_directory, lane.Id, [job.Id], 0, Ct);
        await boards.CreateCardAsync(_directory, new(lane.Id, "Queue me", "", null, "medium", null, [], false), Ct);
        var due = DateTime.UtcNow.AddMinutes(2);
        var runId = Assert.Single(await jobs.EnqueueDueSchedulesAsync(due, Ct));
        Assert.Equal(job.Id, (await jobs.GetRunAsync(runId, Ct))!.JobId);
        Assert.Empty(await boards.GetDueLaneAutomationsAsync(due, Ct));
    }

    [Fact]
    public async Task EveryHostUsesBoardDb_AndLeavesLegacyTablesAndPendingEventsUntouched()
    {
        // Model an older install that still has Board tables and a due event in state.db.
        var legacy = new BoardStore(StateConnectionString, StateConnectionString);
        var legacyJobs = new JobStore(StateConnectionString, legacy);
        using var state = SqliteConnectionFactory.Open(StateConnectionString);
        Execute(state, SqlStrings.CreateEnvironmentsTable);
        var job = await legacyJobs.CreateJobAsync(new("Legacy job", _directory, LLM.NotSet, null, "", null, true, [], Actions:
            [new(null, JobActionKind.Script, ScriptPath: "check.py", ScriptRuntime: JobScriptRuntime.Python, ApprovedHash: "pinned")]), Ct);
        var legacyBoard = await legacy.CreateBoardAsync(_directory, "Old board", Ct);
        var lane = (await legacy.GetColumnsAsync(_directory, Ct, legacyBoard.Id))[0];
        await legacy.SaveLaneAutomationAsync(_directory, lane.Id, [job.Id], 0, Ct);
        var oldCard = await legacy.CreateCardAsync(_directory, new(lane.Id, "Keep in state", "Old description", null, "medium", null, [], false), Ct);
        Execute(state, "DELETE FROM SchemaMigrations WHERE Component='board' AND Version=9;");
        var stateVersion = Scalar(state, "PRAGMA data_version;");

        var services = new ServiceCollection();
        services.AddSqliteStateStorage(_ => new SqliteStoragePaths(StatePath), consumeBoardEvents: true);
        using var root = services.BuildServiceProvider();
        var boards = root.GetRequiredService<IBoardStore>();
        Assert.Empty(await boards.GetBoardsAsync(_directory, Ct));
        Assert.Null(await boards.FindCardAsync(_directory, oldCard.Id, Ct));
        var board = await boards.CreateBoardAsync(_directory, "New board", Ct);
        var newLane = (await boards.GetColumnsAsync(_directory, Ct, board.Id))[0];
        var card = await boards.CreateCardAsync(_directory, new(newLane.Id, "New card", "New description", null, "medium", null, [], false), Ct);
        var file = await boards.AddAttachmentContentAsync(_directory, card.Id, "scope.txt", "text/plain", "scope"u8.ToArray(), Ct);
        await boards.AddCommentAsync(_directory, card.Id, BoardAuthor.User(), "Comment", Ct);
        await boards.AddNoteAsync(_directory, card.Id, BoardAuthor.Agent("agent", "codex", "session"), "Note", Ct);

        // Stdio's focused registration must see exactly the dashboard's Board.
        var childServices = new ServiceCollection();
        childServices.AddSqliteBoardStorage(_ => StatePath);
        using var child = childServices.BuildServiceProvider();
        var childStore = child.GetRequiredService<IBoardStore>();
        Assert.Equal(card.Id, (await childStore.FindCardAsync(_directory, card.Key, Ct))!.Id);
        Assert.Equal("scope"u8.ToArray(), (await childStore.GetAttachmentContentAsync(_directory, card.Id, file!.Id, Ct))!.Content);
        await childStore.UpdateCardAsync(_directory, card.Id, new(Title: "Edited by child"), Ct);
        Assert.Equal("Edited by child", (await boards.FindCardAsync(_directory, card.Id, Ct))!.Title);

        Assert.Empty(await root.GetRequiredService<IJobStore>().EnqueueDueSchedulesAsync(DateTime.UtcNow.AddMinutes(2), Ct));
        Assert.Empty(await SqliteStorage.CreateJobStore(StatePath).EnqueueDueSchedulesAsync(DateTime.UtcNow.AddMinutes(2), Ct));
        Assert.Equal(stateVersion, Scalar(state, "PRAGMA data_version;"));
        Assert.Equal("Keep in state", Scalar(state, "SELECT Title FROM BoardCards;"));
        Assert.Equal(1L, Scalar(state, "SELECT COUNT(*) FROM BoardPendingAutomations;"));
        Assert.Equal(9L, Scalar(state, "SELECT COUNT(*) FROM SchemaMigrations WHERE Component='board';"));
        using var boardDb = OpenBoard();
        Assert.Equal(0L, Scalar(boardDb, "SELECT COUNT(*) FROM sqlite_schema WHERE name IN ('Jobs','Sessions','ChatSummary');"));
        Assert.Null(Scalar(boardDb, "PRAGMA foreign_key_check;"));
    }

    [Fact]
    public async Task StateWriterDoesNotBlockBoardStartupOrCardAndAttachmentWrites()
    {
        StateDatabaseSchema.Ensure(StateConnectionString);
        using var state = SqliteConnectionFactory.Open(StateConnectionString);
        using var heldWriter = state.BeginTransaction(deferred: false);
        var boards = SqliteStorage.CreateBoardStore(StatePath);
        var board = await boards.CreateBoardAsync(_directory, "Unblocked", Ct);
        var lane = (await boards.GetColumnsAsync(_directory, Ct, board.Id))[0];
        var card = await boards.CreateCardAsync(_directory, new(lane.Id, "While logging", "", null, "medium", null, [], false), Ct);
        await boards.AddAttachmentContentAsync(_directory, card.Id, "report.txt", "text/plain", "report"u8.ToArray(), Ct);
        Assert.Single((await boards.GetCardDetailAsync(_directory, card.Id, Ct))!.Attachments);
        Assert.Equal(0L, Scalar(state, "SELECT COUNT(*) FROM sqlite_schema WHERE type='table' AND name LIKE 'Board%';", heldWriter));
    }

    [Fact]
    public async Task SessionOutcomesRemainInState_AndBoardSessionAttributionSurvivesHistoryRemoval()
    {
        using var state = SqliteConnectionFactory.Open(StateConnectionString);
        Execute(state, """
            CREATE TABLE Sessions(Id TEXT PRIMARY KEY, Cli TEXT, EnvironmentName TEXT, EndedUTC TEXT, ExitCode INTEGER);
            CREATE TABLE ChatSummary(SessionId TEXT PRIMARY KEY, SummaryText TEXT);
            INSERT INTO Sessions VALUES('session', 'codex', 'Reviewer', '2026-09-20T12:00:00Z', 0);
            INSERT INTO ChatSummary VALUES('session', 'Work completed');
            """);
        var boards = SqliteStorage.CreateBoardStore(StatePath);
        Assert.Equal("Reviewer", (await boards.FindSessionAuthorAsync("session", Ct))!.Label);
        var outcome = await boards.FindSessionOutcomeAsync("session", Ct);
        Assert.Equal(0, outcome!.ExitCode);
        Assert.Equal("Work completed", outcome.Summary);
        var board = await boards.CreateBoardAsync(_directory, "Main", Ct);
        var lane = (await boards.GetColumnsAsync(_directory, Ct, board.Id))[0];
        var card = await boards.CreateCardAsync(_directory, new(lane.Id, "Linked", "", null, "medium", null, [], false), Ct);
        await boards.LinkSessionAsync(_directory, card.Id, "session", null, "base:codex", "codex", "Saved author", "mcp", Ct);
        Execute(state, "DELETE FROM Sessions; DELETE FROM ChatSummary;");
        Assert.Null(await boards.FindSessionOutcomeAsync("session", Ct));
        Assert.Equal("Saved author", (await boards.FindSessionAuthorAsync("session", Ct))!.Label);
    }

    private SqliteConnection OpenBoard() => SqliteConnectionFactory.Open(new SqliteConnectionStringBuilder { DataSource = BoardPath, Pooling = false }.ToString());

    private static object? Scalar(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
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
        Directory.Delete(_directory, true);
    }
}
