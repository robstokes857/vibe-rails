using Microsoft.Data.Sqlite;
using Moq;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services;
using VibeRails.Services.Board;
using Xunit;

namespace Tests.Services.Board;

public sealed partial class BoardSettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vb16-" + Guid.NewGuid().ToString("N"));
    private readonly string _connectionString;
    private readonly string _stateConnectionString;
    private readonly BoardStore _boards;
    private readonly JobStore _jobs;
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public BoardSettingsTests()
    {
        Directory.CreateDirectory(_root);
        _connectionString = $"Data Source={Path.Combine(_root, "board.db")}";
        _stateConnectionString = $"Data Source={Path.Combine(_root, "state.db")}";
        _boards = new(_connectionString, _stateConnectionString);
        _jobs = new(_stateConnectionString, _boards);
        using var connection = new SqliteConnection(_stateConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = SqlStrings.CreateEnvironmentsTable;
        command.ExecuteNonQuery();
    }

    private Task<JobDefinitionRecord> Job(string name = "Review", string? project = null) =>
        _jobs.CreateJobAsync(new(name, project ?? _root, LLM.NotSet, null, "", null, true, [], Actions:
            [new(null, JobActionKind.Script, ScriptPath: "check.py", ScriptRuntime: JobScriptRuntime.Python, ApprovedHash: "pinned")]), Ct);

    private async Task<(string Board, string A, string B, string C)> Lanes()
    {
        var board = await _boards.CreateBoardAsync(_root, "Main", Ct);
        var columns = await _boards.GetColumnsAsync(_root, Ct, board.Id);
        return (board.Id, columns[0].Id, columns[1].Id, columns[2].Id);
    }

    private Task<BoardCardRecord> Card(string lane) =>
        _boards.CreateCardAsync(_root, new(lane, "Card", "", null, "medium", null, [], false), Ct);

    private async Task<long> Due(string card)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT MAX(DueUnixMs) FROM (
                SELECT DueUnixMs FROM BoardPendingAutomations WHERE CardId = $card
                UNION ALL SELECT DueUnixMs FROM BoardPendingAdditionalAutomations WHERE CardId = $card);
            """;
        command.Parameters.AddWithValue("$card", card);
        var result = await command.ExecuteScalarAsync(Ct);
        return result is null or DBNull ? 0 : Convert.ToInt64(result);
    }

    private Task<IReadOnlyList<string>> Tick(long unixMs, JobStore? store = null) =>
        (store ?? _jobs).EnqueueDueSchedulesAsync(DateTimeOffset.FromUnixTimeMilliseconds(unixMs).UtcDateTime, Ct);

    [Fact]
    public async Task Context_IsPerBoard_Persists_Cascades_AndRejectsStaleWrites()
    {
        var (board, _, _, _) = await Lanes();
        var other = await _boards.CreateBoardAsync(_root, "Other", Ct);
        var service = new BoardService(_boards, Mock.Of<IBoardCommitService>(), new NullBoardLiveSessionProbe());
        var empty = await service.GetContextSettingsAsync(_root, board, Ct);
        Assert.Equal(0, empty!.Revision);
        var context = new BoardContextSettings("  default message  ", [new("bug", "append", "Reproduce first")]);
        var saved = await service.SaveContextSettingsAsync(_root, board, new(context, 0), Ct);
        Assert.Equal("default message", saved!.Context.DefaultMessage);
        Assert.Equal(1, saved.Revision);
        Assert.Equal(saved.Context.DefaultMessage, (await new BoardStore(_connectionString, _stateConnectionString).GetContextSettingsAsync(_root, board, Ct))!.Context.DefaultMessage);
        Assert.Empty((await service.GetContextSettingsAsync(_root, other.Id, Ct))!.Context.DefaultMessage);
        Assert.Null(await service.GetContextSettingsAsync(_root + "-foreign", board, Ct));
        Assert.Null(await service.SaveContextSettingsAsync(_root + "-foreign", board, new(context, 0), Ct));
        await Assert.ThrowsAsync<BoardConflictException>(() => service.SaveContextSettingsAsync(_root, board, new(context, 0), Ct));
        await Assert.ThrowsAsync<BoardValidationException>(() => service.SaveContextSettingsAsync(_root, board, new(context with { DefaultMessage = new('x', 4001) }, 1), Ct));
        await Assert.ThrowsAsync<BoardValidationException>(() => service.SaveContextSettingsAsync(_root, board, new(context with { TypeOverrides = [new("bug", "oops", "")] }, 1), Ct));
        await Assert.ThrowsAsync<BoardValidationException>(() => service.SaveContextSettingsAsync(_root, board, new(context with { TypeOverrides = [new("bug", "append", ""), new("bug", "replace", "")] }, 1), Ct));
        await _boards.DeleteBoardAsync(_root, board, Ct);
        Assert.Null(await service.GetContextSettingsAsync(_root, board, Ct));
    }

    [Fact]
    public async Task RapidMoves_QueueOnlyFinalLane_After60Seconds_OnceAcrossRestartsAndRoots()
    {
        var (_, a, b, c) = await Lanes();
        var jobA = await Job("A");
        var jobB = await Job("B");
        var jobC = await Job("C");
        await _boards.SaveLaneAutomationAsync(_root, a, [jobA.Id], 0, Ct);
        await _boards.SaveLaneAutomationAsync(_root, b, [jobB.Id], 0, Ct);
        await _boards.SaveLaneAutomationAsync(_root, c, [jobC.Id], 0, Ct);
        var before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var card = await Card(a);
        await _boards.MoveCardAsync(_root, card.Id, b, null, Ct);
        await _boards.UpdateCardAsync(_root, card.Id, new(ColumnId: c), Ct);
        var due = await Due(card.Id);
        Assert.InRange(due, before + 59_999, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 60_001);
        Assert.Empty(await Tick(due - 1));
        // Reopening independent stores models roots contending after the original writer exits.
        var results = await Task.WhenAll(Tick(due, ReopenJobs()), Tick(due, ReopenJobs()));
        var runId = Assert.Single(results.SelectMany(x => x));
        var run = (await _jobs.GetRunAsync(runId, Ct))!;
        Assert.Equal(jobC.Id, run.JobId);
        Assert.Equal(JobTriggerKind.BoardLane, run.TriggerKind);
        Assert.StartsWith("board-lane:VB-1:", run.TriggerKey);
        Assert.Single(run.Actions!);
        Assert.Empty(await Tick(due + 100_000));
    }

    [Fact]
    public async Task SameLaneEditsDoNotResetTimer_AndMovingToUnconfiguredLaneCancels()
    {
        var (_, a, b, _) = await Lanes();
        var job = await Job();
        await _boards.SaveLaneAutomationAsync(_root, a, [job.Id], 0, Ct);
        var card = await Card(a);
        var due = await Due(card.Id);
        await _boards.MoveCardAsync(_root, card.Id, a, 0, Ct);
        await _boards.UpdateCardAsync(_root, card.Id, new(Title: "Changed", Blocked: true), Ct);
        Assert.Equal(due, await Due(card.Id));
        await _boards.MoveCardAsync(_root, card.Id, b, null, Ct);
        Assert.Empty(await Tick(due + 1));
        await _boards.MoveCardAsync(_root, card.Id, a, null, Ct);
        Assert.Single(await Tick(await Due(card.Id)));
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("deleted-job")]
    [InlineData("deleted-card")]
    [InlineData("removed-setting")]
    [InlineData("overlap")]
    public async Task PendingTriggers_RevalidateAndConsumeWithoutLaterSurprises(string reason)
    {
        var (_, a, _, _) = await Lanes();
        var job = await Job();
        await _boards.SaveLaneAutomationAsync(_root, a, [job.Id], 0, Ct);
        var card = await Card(a);
        var due = await Due(card.Id);
        if (reason == "disabled")
            await _jobs.UpdateJobAsync(job.Id, new(job.Name, _root, LLM.NotSet, null, "", null, false, []), Ct);
        if (reason == "deleted-job") await _jobs.SoftDeleteJobAsync(job.Id, Ct);
        if (reason == "deleted-card") await _boards.DeleteCardAsync(_root, card.Id, Ct);
        if (reason == "removed-setting") await _boards.SaveLaneAutomationAsync(_root, a, [], 1, Ct);
        if (reason == "overlap") Assert.NotNull(await _jobs.EnqueueManualRunAsync(job.Id, Ct));
        Assert.Empty(await Tick(due));
        Assert.Equal(0, await Due(card.Id));
    }

    [Fact]
    public async Task LaneSettings_RejectCrossProjectJobsAndStaleSaves_AndDoNotTriggerExistingCards()
    {
        var (_, a, _, _) = await Lanes();
        var card = await Card(a);
        var foreign = await Job(project: _root + "-other");
        await Assert.ThrowsAsync<BoardValidationException>(() => _boards.SaveLaneAutomationAsync(_root, a, [foreign.Id], 0, Ct));
        var job = await Job();
        Assert.Null(await _boards.SaveLaneAutomationAsync(_root + "-other", a, [job.Id], 0, Ct));
        await _boards.SaveLaneAutomationAsync(_root, a, [job.Id], 0, Ct);
        await Assert.ThrowsAsync<BoardConflictException>(() => _boards.SaveLaneAutomationAsync(_root, a, [], 0, Ct));
        Assert.Equal(0, await Due(card.Id));
    }

    [Fact]
    public async Task UpgradeFromBoard5_PreservesCards_AndLegacyLaneWritesStillWork()
    {
        var (board, a, b, _) = await Lanes();
        var card = await Card(a);
        await RemoveBoard7();
        await using (var connection = new SqliteConnection(_connectionString))
        {
            await connection.OpenAsync(Ct);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                DROP TRIGGER BoardCards_LaneAutomation_Insert;
                DROP TRIGGER BoardCards_LaneAutomation_Move;
                DROP TABLE BoardPendingAutomations;
                DROP TABLE BoardLaneAutomations;
                DROP TABLE BoardContextSettings;
                DELETE FROM SchemaMigrations WHERE Component = 'board' AND Version = 6;
                """;
            await command.ExecuteNonQueryAsync(Ct);
        }
        var upgraded = new BoardStore(_connectionString, _stateConnectionString);
        Assert.Equal(card.Description, (await upgraded.FindCardAsync(_root, card.Id, Ct))!.Description);
        Assert.Empty((await upgraded.GetContextSettingsAsync(_root, board, Ct))!.Context.DefaultMessage);
        Assert.Null((await upgraded.GetLaneAutomationAsync(_root, b, Ct))!.JobId);
        var job = await Job();
        await upgraded.SaveLaneAutomationAsync(_root, b, [job.Id], 0, Ct);
        // Previous binaries only know these card columns. Their UPDATE still succeeds and
        // participates in durable debounce without needing to know the new settings tables.
        await using (var connection = new SqliteConnection(_connectionString))
        {
            await connection.OpenAsync(Ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE BoardCards SET ColumnId = $lane, UpdatedUTC = $now WHERE Id = $id;";
            command.Parameters.AddWithValue("$lane", b);
            command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$id", card.Id);
            Assert.Equal(1, await command.ExecuteNonQueryAsync(Ct));
        }
        Assert.Single(await Tick(await Due(card.Id)));
    }

    [Fact]
    public async Task CrossBoardMoveAndLaneDeletion_UseTheFinalDestinationAutomation()
    {
        var (_, a, _, _) = await Lanes();
        var second = await _boards.CreateBoardAsync(_root, "Second", Ct);
        var lanes = await _boards.GetColumnsAsync(_root, Ct, second.Id);
        var job = await Job();
        var additional = await Job("Additional");
        await _boards.SaveLaneAutomationAsync(_root, lanes[0].Id, [job.Id, additional.Id], 0, Ct);
        var card = await Card(a);
        await _boards.MoveCardAsync(_root, card.Id, lanes[1].Id, null, Ct);
        await _boards.DeleteColumnAsync(_root, lanes[1].Id, Ct);
        Assert.Equal(lanes[0].Id, (await _boards.FindCardAsync(_root, card.Id, Ct))!.ColumnId);
        Assert.Equal(2, (await Tick(await Due(card.Id))).Count);
    }

    [Fact]
    public async Task RunCommittedBeforeAcknowledgementFailure_IsNotDuplicatedOnRetryEvenAfterCompletion()
    {
        var (_, lane, _, _) = await Lanes();
        var job = await Job();
        await _boards.SaveLaneAutomationAsync(_root, lane, [job.Id], 0, Ct);
        var card = await Card(lane);
        var due = await Due(card.Id);
        var unavailable = new Mock<IBoardStore>(MockBehavior.Strict);
        unavailable.Setup(store => store.GetDueLaneAutomationsAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Returns<DateTime, CancellationToken>(_boards.GetDueLaneAutomationsAsync);
        unavailable.Setup(store => store.AcknowledgeLaneAutomationAsync(It.IsAny<BoardLaneAutomationEvent>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("Board unavailable after local commit"));
        // The acknowledgement failure is logged and the cycle continues; the run is already committed.
        Assert.Single(await Tick(due, new JobStore(_stateConnectionString, unavailable.Object)));
        var run = Assert.Single(await _jobs.GetRunsAsync(cancellationToken: Ct));
        Assert.Single((await _jobs.GetRunAsync(run.Id, Ct))!.Actions!);
        Assert.Equal(due, await Due(card.Id));
        await _jobs.CompleteRunAsync(run.Id, JobRunStatus.Succeeded, 0, null, Ct);

        Assert.Empty(await Tick(due + 1, ReopenJobs()));
        Assert.Single(await _jobs.GetRunsAsync(cancellationToken: Ct));
        Assert.Equal(0, await Due(card.Id));
    }

    [Fact]
    public async Task AcknowledgingAnOlderEntry_DoesNotDeleteARepeatedEntryIntoTheSameLane()
    {
        var (_, lane, other, _) = await Lanes();
        var job = await Job();
        await _boards.SaveLaneAutomationAsync(_root, lane, [job.Id], 0, Ct);
        var card = await Card(lane);
        var now = DateTime.UtcNow.AddMinutes(2);
        var first = Assert.Single(await _boards.GetDueLaneAutomationsAsync(now, Ct));
        await _boards.MoveCardAsync(_root, card.Id, other, null, Ct);
        await _boards.MoveCardAsync(_root, card.Id, lane, null, Ct);
        await _boards.AcknowledgeLaneAutomationAsync(first, Ct);

        var next = Assert.Single(await _boards.GetDueLaneAutomationsAsync(now, Ct));
        Assert.NotEqual(first.EventKey, next.EventKey);
        Assert.Single(await _jobs.EnqueueDueSchedulesAsync(now, Ct));
    }

    [Fact]
    public async Task BoardReadFailure_IsLoggedAndScheduledRunsStillEnqueue()
    {
        // board.db locked, damaged or newer must not veto the schedule loop that follows the drain.
        var job = await _jobs.CreateJobAsync(new("Nightly", _root, LLM.NotSet, null, "", null, true,
            [new(JobTriggerKind.Schedule, JobScheduleKind.Interval, 5)], Actions:
            [new(null, JobActionKind.Script, ScriptPath: "check.py", ScriptRuntime: JobScriptRuntime.Python, ApprovedHash: "pinned")]), Ct);
        var unavailable = new Mock<IBoardStore>(MockBehavior.Strict);
        unavailable.Setup(store => store.GetDueLaneAutomationsAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("board.db is locked"));

        var runId = Assert.Single(await new JobStore(_stateConnectionString, unavailable.Object)
            .EnqueueDueSchedulesAsync(DateTime.UtcNow.AddMinutes(10), Ct));
        Assert.Equal(job.Id, (await _jobs.GetRunAsync(runId, Ct))!.JobId);
    }

    [Fact]
    public async Task AcknowledgementFailureOnOneEntry_DoesNotStarveTheEntriesAfterIt()
    {
        var (_, a, b, _) = await Lanes();
        var first = await Job("First");
        var second = await Job("Second");
        await _boards.SaveLaneAutomationAsync(_root, a, [first.Id], 0, Ct);
        await _boards.SaveLaneAutomationAsync(_root, b, [second.Id], 0, Ct);
        var cardA = await Card(a);
        var cardB = await Card(b);
        var due = Math.Max(await Due(cardA.Id), await Due(cardB.Id));
        var flaky = new Mock<IBoardStore>(MockBehavior.Strict);
        flaky.Setup(store => store.GetDueLaneAutomationsAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Returns<DateTime, CancellationToken>(_boards.GetDueLaneAutomationsAsync);
        flaky.Setup(store => store.AcknowledgeLaneAutomationAsync(It.Is<BoardLaneAutomationEvent>(e => e.JobId == first.Id), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("Poison entry"));
        flaky.Setup(store => store.AcknowledgeLaneAutomationAsync(It.Is<BoardLaneAutomationEvent>(e => e.JobId != first.Id), It.IsAny<CancellationToken>()))
            .Returns<BoardLaneAutomationEvent, CancellationToken>(_boards.AcknowledgeLaneAutomationAsync);

        Assert.Equal(2, (await Tick(due, new JobStore(_stateConnectionString, flaky.Object))).Count);
        Assert.Equal(2, (await _jobs.GetRunsAsync(cancellationToken: Ct)).Count);
        Assert.NotEqual(0, await Due(cardA.Id)); // Still pending: the next cycle retries it.
        Assert.Equal(0, await Due(cardB.Id));    // Acknowledged despite the earlier failure.
    }

    public void Dispose()
    {
        SqliteConnection.ClearPool(new SqliteConnection(_connectionString));
        SqliteConnection.ClearPool(new SqliteConnection(_stateConnectionString));
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private JobStore ReopenJobs() => new(_stateConnectionString, new BoardStore(_connectionString, _stateConnectionString));
}
