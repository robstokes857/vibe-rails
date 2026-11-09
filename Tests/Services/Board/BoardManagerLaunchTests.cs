using Microsoft.Data.Sqlite;
using Moq;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services.Board;
using VibeRails.Services.Terminal;
using VibeRails.Utils;
using Xunit;

namespace Tests.Services.Board;

/// <summary>
/// Launching the board manager (VIBE-52 follow-up, VIBE-204): the AI board manager switch, the
/// board-level prompt with nudged App and Board context, the recorded session, the manager origin on
/// every managed card, one live manager per board, Stop, a card without its own manager handing Start
/// Agent workflow to the board manager, and the server-side worker limit.
/// </summary>
public sealed class BoardManagerLaunchTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"viberails-board-manager-launch-{Guid.NewGuid():N}");
    private readonly string _connectionString;
    private readonly BoardStore _store;
    private readonly Mock<ITerminalTabHostService> _tabs = new(MockBehavior.Strict);
    private readonly Mock<IRepository> _repository = new(MockBehavior.Strict);
    private readonly List<StartTerminalRequest> _requests = [];
    private readonly List<TerminalTabStatusResponse> _liveTabs = [];
    private readonly List<string> _deletedTabs = [];
    private int _tabNumber;
    private bool _nudge;
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public BoardManagerLaunchTests()
    {
        Directory.CreateDirectory(_root);
        _connectionString = $"Data Source={Path.Combine(_root, "state.db")};Mode=ReadWriteCreate;Cache=Shared";
        _store = new BoardStore(_connectionString, _connectionString);
        _tabs.SetupGet(t => t.MaxTabs).Returns(8);
        _tabs.Setup(t => t.ListTabsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => _liveTabs.ToList());
        _tabs.Setup(t => t.CreateTabAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new TerminalTabStatusResponse($"tab-{++_tabNumber}", DateTime.UtcNow, false));
        _tabs.Setup(t => t.DeleteTabAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, CancellationToken>((tabId, _) => { _deletedTabs.Add(tabId); _liveTabs.RemoveAll(tab => tab.TabId == tabId); })
            .ReturnsAsync(true);
        _tabs.Setup(t => t.StartSessionAsync(It.IsAny<string>(), It.IsAny<StartTerminalRequest>(), It.IsAny<CancellationToken>()))
            .Callback<string, StartTerminalRequest, CancellationToken>((_, value, _) => _requests.Add(value))
            .ReturnsAsync((string tabId, StartTerminalRequest request, CancellationToken _) =>
            {
                var session = new TerminalStatusResponse(true, Guid.NewGuid().ToString(), request.Cli, _root);
                _liveTabs.Add(new TerminalTabStatusResponse(tabId, DateTime.UtcNow, true, session.SessionId));
                return session;
            });
    }

    /// <summary>owners says whether a recorded owner process is still running its session (VIBE-262).</summary>
    private BoardLaunchService Launcher(Func<int, DateTime, bool>? owners = null) =>
        new(_store, _tabs.Object, new BoardContextEstimator(
            new BoardService(_store, new Mock<IBoardCommitService>().Object, new NullBoardLiveSessionProbe()), _store),
            launchSettings: () => new Settings { CreateVibeStoryTracking = _nudge }, sessionOwnerRunning: owners);

    /// <summary>The tab child of another VibeRails window on this machine.</summary>
    private const int OtherWindowPid = 4242;

    private static bool OtherWindow(int pid, DateTime started) => pid == OtherWindowPid;

    /// <summary>A state.db session row, as a tab child records it (the fixture shares one file for both databases).</summary>
    private async Task RecordSessionAsync(string sessionId, int? ownerPid = OtherWindowPid, DateTime? endedUtc = null, DateTime? startedUtc = null)
    {
        await using var db = new SqliteConnection(_connectionString);
        await db.OpenAsync(Ct);
        await using var command = db.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS Sessions (Id TEXT PRIMARY KEY, Cli TEXT, StartedUTC TEXT NOT NULL, EndedUTC TEXT, ExitCode INTEGER, OwnerPid INTEGER);
            INSERT INTO Sessions (Id, Cli, StartedUTC, EndedUTC, OwnerPid) VALUES ($id, 'claude', $started, $ended, $pid);
            """;
        command.Parameters.AddWithValue("$id", sessionId);
        command.Parameters.AddWithValue("$started", (startedUtc ?? DateTime.UtcNow.AddMinutes(-1)).ToString("O"));
        command.Parameters.AddWithValue("$ended", endedUtc is { } ended ? ended.ToString("O") : DBNull.Value);
        command.Parameters.AddWithValue("$pid", ownerPid is { } pid ? pid : DBNull.Value);
        await command.ExecuteNonQueryAsync(Ct);
    }

    /// <summary>Moves a board.db link's recorded time back, as if the agent had been running that long.</summary>
    private async Task BackdateLinkAsync(string table, string column, string sessionId, DateTime when)
    {
        await using var db = new SqliteConnection(_connectionString);
        await db.OpenAsync(Ct);
        await using var command = db.CreateCommand();
        command.CommandText = $"UPDATE {table} SET {column} = $when WHERE SessionId = $id;";
        command.Parameters.AddWithValue("$id", sessionId);
        command.Parameters.AddWithValue("$when", when.ToString("O"));
        Assert.Equal(1, await command.ExecuteNonQueryAsync(Ct));
    }

    private async Task EndSessionAsync(string sessionId)
    {
        await using var db = new SqliteConnection(_connectionString);
        await db.OpenAsync(Ct);
        await using var command = db.CreateCommand();
        command.CommandText = "UPDATE Sessions SET EndedUTC = $ended, ExitCode = 0 WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", sessionId);
        command.Parameters.AddWithValue("$ended", DateTime.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(Ct);
    }

    /// <summary>How prompts print a card: its key, plus the display label when that differs.</summary>
    private static string Label(BoardCardRecord card) => card.DisplayId == card.Key ? card.Key : $"{card.Key} ({card.DisplayId})";

    private static NewBoardCard Card(string title, string? workflow = null, string? manager = null, string? assignee = "base:claude") =>
        new(null, title, "Scope", assignee, "medium", null, [], false, WorkflowMode: workflow ?? BoardWorkflowModes.Manual, ManagerSelection: manager);

    private async Task<string> BoardWithManagerAsync(BaseLlmOptions? options = null, int maxWorkers = 1)
    {
        await _store.EnsureDefaultColumnsAsync(_root, Ct);
        await _store.SetBoardManagerEnabledAsync(true, Ct);
        var boardId = (await _store.GetBoardsAsync(_root, Ct)).Single().Id;
        await _store.SaveBoardManagerAsync(_root, boardId, new("base:codex", options, "Bugs first.", maxWorkers), 0, Ct);
        return boardId;
    }

    /// <summary>A worker session on a card whose terminal is live in this root.</summary>
    private async Task<string> LiveWorkerAsync(BoardCardRecord card, string origin = BoardSessionRecord.WorkerOrigin)
    {
        var sessionId = Guid.NewGuid().ToString();
        var tabId = $"worker-tab-{++_tabNumber}";
        await _store.LinkSessionAsync(_root, card.Id, sessionId, tabId, "base:claude", "claude", "Worker", origin, Ct);
        _liveTabs.Add(new TerminalTabStatusResponse(tabId, DateTime.UtcNow, true, sessionId));
        return sessionId;
    }

    [Fact]
    public async Task LaunchComposesBoardPrompt_RecordsSession_AttachesManagedCards_AndReusesTheLiveManager()
    {
        var options = new BaseLlmOptions(Model: "gpt-6-astra", Effort: "high", Speed: "ultrafast");
        var boardId = await BoardWithManagerAsync(options);
        var managed = await _store.CreateCardAsync(_root, Card("Managed by the board", BoardWorkflowModes.Agent), Ct);
        var own = await _store.CreateCardAsync(_root, Card("Keeps its manager", BoardWorkflowModes.Agent, "base:claude"), Ct);
        var manual = await _store.CreateCardAsync(_root, Card("Manual"), Ct);

        var launched = (await Launcher().LaunchBoardManagerAsync(_root, boardId, Ct))!;
        Assert.False(launched.Reused); Assert.Equal("base:codex", launched.Selection); Assert.Equal("tab-1", launched.TabId);
        var request = Assert.Single(_requests);
        Assert.Equal("codex", request.Cli); Assert.Equal(options, request.BaseLlmOptions); Assert.True(request.AuthorizeBoardTools);
        Assert.Equal(_root, request.WorkingDirectory);
        // The prompt carries the launch's context, so the tab child must not prepend it again.
        Assert.Equal(boardId, request.ContextBoardId); Assert.True(request.ContextIncluded);
        Assert.StartsWith("You are the AI board manager for the VibeRails board", request.InitialPrompt);
        Assert.Contains("await_board_events", request.InitialPrompt);
        Assert.Contains("run one implementation worker at a time across the whole board", request.InitialPrompt);
        Assert.Contains("Manager instructions from the user for this board. Follow them within the role above:\nBugs first.", request.InitialPrompt);
        Assert.Contains($"- {Label(managed)} · lane Backlog", request.InitialPrompt);
        Assert.Contains($"Agent cards with their own manager (not yours): {own.Key}", request.InitialPrompt);
        Assert.DoesNotContain(manual.Key, request.InitialPrompt);
        Assert.Contains("Lanes: Backlog → Ready → Build → Review → Done", request.InitialPrompt);
        Assert.DoesNotContain("Context supplied by the user", request.InitialPrompt);

        var session = Assert.Single(await _store.GetBoardManagerSessionsAsync(_root, boardId, 10, Ct));
        Assert.Equal(launched.SessionId, session.SessionId); Assert.Equal("tab-1", session.TabId);
        var link = Assert.Single((await _store.GetCardDetailAsync(_root, managed.Id, Ct))!.Sessions);
        Assert.Equal(BoardSessionRecord.ManagerOrigin, link.Origin); Assert.Equal(launched.SessionId, link.SessionId);
        Assert.Empty((await _store.GetCardDetailAsync(_root, own.Id, Ct))!.Sessions);
        Assert.Empty((await _store.GetCardDetailAsync(_root, manual.Id, Ct))!.Sessions);

        var live = (await Launcher().FindLiveBoardManagerAsync(_root, boardId, Ct))!;
        Assert.Equal(launched.SessionId, live.SessionId); Assert.Equal("tab-1", live.TabId);
        var again = (await Launcher().LaunchBoardManagerAsync(_root, boardId, Ct))!;
        Assert.True(again.Reused); Assert.Equal(launched.SessionId, again.SessionId);
        Assert.Single(_requests);

        // The tab is gone: the stored session is no longer live and a fresh launch starts another.
        _liveTabs.Clear();
        Assert.Null(await Launcher().FindLiveBoardManagerAsync(_root, boardId, Ct));
        Assert.False((await Launcher().LaunchBoardManagerAsync(_root, boardId, Ct))!.Reused);
        Assert.Equal(2, _requests.Count);
    }

    [Fact]
    public async Task TheNudgeAddsAppAndBoardContext_ToTheManagerPrompt()
    {
        var boardId = await BoardWithManagerAsync();
        await _store.SaveAppContextSettingsAsync("Use British spelling.", 0, Ct);
        _nudge = true;
        await Launcher().LaunchBoardManagerAsync(_root, boardId, Ct);
        Assert.Contains("Context supplied by the user, broadest first:\n\nApp context:\nUse British spelling.", Assert.Single(_requests).InitialPrompt);
    }

    [Fact]
    public async Task StopClosesTheLiveManagersTab_AndOnlyThatTab()
    {
        var boardId = await BoardWithManagerAsync();
        var worker = await _store.CreateCardAsync(_root, Card("Has a worker", BoardWorkflowModes.Agent), Ct);
        await LiveWorkerAsync(worker);
        Assert.False((await Launcher().StopBoardManagerAsync(_root, boardId, Ct))!.Stopped);

        var launched = (await Launcher().LaunchBoardManagerAsync(_root, boardId, Ct))!;
        var stopped = (await Launcher().StopBoardManagerAsync(_root, boardId, Ct))!;
        Assert.True(stopped.Stopped); Assert.Equal(launched.SessionId, stopped.SessionId);
        Assert.Equal([launched.TabId], _deletedTabs);
        Assert.Null(await Launcher().FindLiveBoardManagerAsync(_root, boardId, Ct));
        Assert.Single(_liveTabs);
        Assert.Null(await Launcher().StopBoardManagerAsync(_root, "board_missing", Ct));
    }

    [Fact]
    public async Task StartAgentWorkflowOnABoardManagedCard_StartsTheBoardManager_AndAttachesTheCard()
    {
        var boardId = await BoardWithManagerAsync();
        var card = await _store.CreateCardAsync(_root, Card("Board managed", BoardWorkflowModes.Agent), Ct);

        var response = (await Launcher().LaunchAsync(_root, card.Id, null, Ct))!;
        Assert.Equal("base:codex", response.Selection); Assert.Equal(card.Id, response.CardId);
        var request = Assert.Single(_requests);
        Assert.StartsWith("You are the AI board manager for the VibeRails board", request.InitialPrompt);
        Assert.Equal(BoardSessionRecord.ManagerOrigin, Assert.Single((await _store.GetCardDetailAsync(_root, card.Id, Ct))!.Sessions).Origin);
        Assert.Equal(response.SessionId, Assert.Single(await _store.GetBoardManagerSessionsAsync(_root, boardId, 10, Ct)).SessionId);

        // A card made an Agent card after the manager started joins it on its own Start.
        var late = await _store.CreateCardAsync(_root, Card("Joined later", BoardWorkflowModes.Agent), Ct);
        var joined = (await Launcher().LaunchAsync(_root, late.Id, null, Ct))!;
        Assert.Equal(response.SessionId, joined.SessionId);
        Assert.Single(_requests);
        Assert.Equal(response.SessionId, Assert.Single((await _store.GetCardDetailAsync(_root, late.Id, Ct))!.Sessions).SessionId);

        // A card with its own manager still gets its own.
        var ownManaged = await _store.CreateCardAsync(_root, Card("Own", BoardWorkflowModes.Agent, "base:codex"), Ct);
        await Launcher().LaunchAsync(_root, ownManaged.Id, null, Ct);
        Assert.StartsWith("You are the manager agent for this card's Agent workflow", _requests[^1].InitialPrompt);
    }

    [Fact]
    public async Task WithTheSwitchOff_NothingStartsABoardManager()
    {
        var boardId = await BoardWithManagerAsync();
        var card = await _store.CreateCardAsync(_root, Card("Board managed", BoardWorkflowModes.Agent), Ct);
        await _store.SetBoardManagerEnabledAsync(false, Ct);

        var board = await Assert.ThrowsAsync<BoardConflictException>(() => Launcher().LaunchBoardManagerAsync(_root, boardId, Ct));
        Assert.Equal(BoardManagerService.DisabledMessage, board.Message);
        var fromCard = await Assert.ThrowsAsync<BoardValidationException>(() => Launcher().LaunchAsync(_root, card.Id, null, Ct));
        Assert.Contains("AI board manager is off", fromCard.Message);
        Assert.Empty(_requests);
    }

    [Fact]
    public async Task WithoutABoardManager_ACardWithoutItsOwnCannotStart_AndTheBoardLaunchExplains()
    {
        await _store.EnsureDefaultColumnsAsync(_root, Ct);
        await _store.SetBoardManagerEnabledAsync(true, Ct);
        var boardId = (await _store.GetBoardsAsync(_root, Ct)).Single().Id;
        var failure = await Assert.ThrowsAsync<BoardValidationException>(() => Launcher().LaunchBoardManagerAsync(_root, boardId, Ct));
        Assert.Contains("Board settings", failure.Message);
        Assert.Null(await Launcher().LaunchBoardManagerAsync(_root, "board_missing", Ct));
        Assert.Null(await Launcher().FindLiveBoardManagerAsync(_root, boardId, Ct));
        Assert.Empty(_requests);
    }

    [Theory]
    [InlineData(BoardSessionRecord.WorkerOrigin)]
    [InlineData(BoardSessionRecord.ChatWorkOrigin)]
    public async Task TheWorkerLimit_HoldsAcrossTheBoardsManagedCards_ButNotReviewsPlannersOrOwnManagers(string origin)
    {
        var boardId = await BoardWithManagerAsync();
        var busy = await _store.CreateCardAsync(_root, Card("Busy", BoardWorkflowModes.Agent), Ct);
        var next = await _store.CreateCardAsync(_root, Card("Next", BoardWorkflowModes.Agent), Ct);
        var own = await _store.CreateCardAsync(_root, Card("Own manager", BoardWorkflowModes.Agent, "base:codex"), Ct);
        var running = await LiveWorkerAsync(busy, origin);

        var refused = await Assert.ThrowsAsync<BoardConflictException>(() => Launcher().LaunchAsync(_root, next.Id, null, Ct, intent: "worker"));
        Assert.Contains("at most 1 implementation worker at a time", refused.Message);
        Assert.Contains(busy.DisplayId ?? busy.Key, refused.Message);
        Assert.Equal(running, refused.SessionId);
        Assert.Empty(_requests);
        Assert.Single((await Launcher().GetBoardManagerLiveStateAsync(_root, boardId, Ct)).Workers);

        // A card with its own manager is outside the board manager's limit.
        Assert.NotNull(await Launcher().LaunchAsync(_root, own.Id, null, Ct, intent: "worker"));
        Assert.Single(_requests);

        // A planning worker never edits, so it does not count either.
        await LiveWorkerAsync(next, BoardSessionRecord.PlanningWorkerOrigin);
        Assert.Single((await Launcher().GetBoardManagerLiveStateAsync(_root, boardId, Ct)).Workers);

        // Raising the limit makes room; the worker that just exited frees its slot as well.
        var saved = (await _store.GetBoardManagerAsync(_root, boardId, Ct))!;
        await _store.SaveBoardManagerAsync(_root, boardId, saved.Settings with { MaxWorkers = 2 }, saved.Revision, Ct);
        var third = await _store.CreateCardAsync(_root, Card("Third", BoardWorkflowModes.Agent), Ct);
        Assert.NotNull(await Launcher().LaunchAsync(_root, third.Id, null, Ct, intent: "worker"));
        Assert.Equal(2, (await Launcher().GetBoardManagerLiveStateAsync(_root, boardId, Ct)).Workers.Count);

        // Full again; with the switch off the board manager's limit no longer applies.
        var fourth = await _store.CreateCardAsync(_root, Card("Fourth", BoardWorkflowModes.Agent), Ct);
        await Assert.ThrowsAsync<BoardConflictException>(() => Launcher().LaunchAsync(_root, fourth.Id, null, Ct, intent: "worker"));
        await _store.SetBoardManagerEnabledAsync(false, Ct);
        Assert.NotNull(await Launcher().LaunchAsync(_root, fourth.Id, null, Ct, intent: "worker"));
    }

    [Fact]
    public async Task TwoWorkerLaunchesAtOnce_OnDifferentManagedCards_StartOnlyOne()
    {
        await BoardWithManagerAsync();
        var first = await _store.CreateCardAsync(_root, Card("First", BoardWorkflowModes.Agent), Ct);
        var second = await _store.CreateCardAsync(_root, Card("Second", BoardWorkflowModes.Agent), Ct);
        // The first worker's session start is held open, before it is linked to its card.
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _tabs.Setup(t => t.StartSessionAsync(It.IsAny<string>(), It.IsAny<StartTerminalRequest>(), It.IsAny<CancellationToken>()))
            .Returns(async (string tabId, StartTerminalRequest request, CancellationToken _) =>
            {
                _requests.Add(request);
                entered.TrySetResult();
                await release.Task;
                var session = new TerminalStatusResponse(true, Guid.NewGuid().ToString(), request.Cli, _root);
                _liveTabs.Add(new TerminalTabStatusResponse(tabId, DateTime.UtcNow, true, session.SessionId));
                return session;
            });

        var launching = Launcher().LaunchAsync(_root, first.Id, null, Ct, intent: "worker");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var racing = await Assert.ThrowsAsync<BoardConflictException>(() => Launcher().LaunchAsync(_root, second.Id, null, Ct, intent: "worker"));
        Assert.Contains("Another implementation worker is starting on this board", racing.Message);
        release.SetResult();
        Assert.NotNull(await launching);
        Assert.Single(_requests);

        // Once the first is linked and live, the limit itself refuses the second; the slot was released.
        var limited = await Assert.ThrowsAsync<BoardConflictException>(() => Launcher().LaunchAsync(_root, second.Id, null, Ct, intent: "worker"));
        Assert.Contains("at most 1 implementation worker at a time", limited.Message);
        Assert.Single(_requests);

        // A refused launch releases the slot too: with room again, the next one starts.
        _liveTabs.Clear();
        Assert.NotNull(await Launcher().LaunchAsync(_root, second.Id, null, Ct, intent: "worker"));
        Assert.Equal(2, _requests.Count);
    }

    [Fact]
    public async Task AManagerRunningInAnotherWindow_RefusesASecondOne_ShowsInTheStatus_AndStopsCountingWithItsWindow()
    {
        var boardId = await BoardWithManagerAsync();
        var card = await _store.CreateCardAsync(_root, Card("Managed", BoardWorkflowModes.Agent), Ct);
        var theirs = Guid.NewGuid().ToString();
        await _store.RecordBoardManagerSessionAsync(_root, boardId, theirs, "their-tab", "base:codex", "codex", Ct);
        await RecordSessionAsync(theirs);

        var refused = await Assert.ThrowsAsync<BoardConflictException>(() => Launcher(OtherWindow).LaunchBoardManagerAsync(_root, boardId, Ct));
        Assert.Equal(BoardLaunchService.ManagerElsewhereMessage, refused.Message);
        Assert.Null(refused.SessionId);
        // Start Agent workflow on one of its cards says the same and starts nothing either.
        var fromCard = await Assert.ThrowsAsync<BoardConflictException>(() => Launcher(OtherWindow).LaunchAsync(_root, card.Id, null, Ct));
        Assert.Equal(BoardLaunchService.ManagerElsewhereMessage, fromCard.Message);
        Assert.Empty(_requests);

        var state = await Launcher(OtherWindow).GetBoardManagerLiveStateAsync(_root, boardId, Ct);
        Assert.Null(state.Active);
        Assert.Equal(theirs, state.Elsewhere?.SessionId);
        var status = (await new BoardManagerService(_store, Launcher(OtherWindow)).GetAsync(_root, boardId, Ct))!;
        Assert.Null(status.Active);
        Assert.Equal(theirs, status.Elsewhere?.SessionId);
        Assert.Equal("base:codex", status.Elsewhere?.Selection);
        // There is no tab here to close.
        Assert.False((await Launcher(OtherWindow).StopBoardManagerAsync(_root, boardId, Ct))!.Stopped);
        Assert.Empty(_deletedTabs);

        // That window closed: its tab child is gone, so this window starts its own manager.
        Assert.Null((await Launcher((_, _) => false).GetBoardManagerLiveStateAsync(_root, boardId, Ct)).Elsewhere);
        var launched = (await Launcher((_, _) => false).LaunchBoardManagerAsync(_root, boardId, Ct))!;
        Assert.False(launched.Reused);
        Assert.Single(_requests);
        // A live manager here wins over any record of another window's.
        Assert.True((await Launcher(OtherWindow).LaunchBoardManagerAsync(_root, boardId, Ct))!.Reused);
    }

    [Fact]
    public async Task AnotherWindowsManager_NeedsAnOpenSessionAndAnotherProcess_AndThisRootsTabsAreNeverElsewhere()
    {
        var boardId = await BoardWithManagerAsync();
        async Task<string> Recorded(string tabId, int? ownerPid = OtherWindowPid, DateTime? endedUtc = null)
        {
            var sessionId = Guid.NewGuid().ToString();
            await _store.RecordBoardManagerSessionAsync(_root, boardId, sessionId, tabId, "base:codex", "codex", Ct);
            await RecordSessionAsync(sessionId, ownerPid, endedUtc);
            return sessionId;
        }
        async Task<string?> Elsewhere(Func<int, DateTime, bool> owners) =>
            (await Launcher(owners).GetBoardManagerLiveStateAsync(_root, boardId, Ct)).Elsewhere?.SessionId;

        // Its end is recorded, or it never recorded an owner: not running anywhere.
        await Recorded("ended-tab", endedUtc: DateTime.UtcNow);
        await Recorded("no-owner-tab", ownerPid: null);
        Assert.Null(await Elsewhere((_, _) => true));
        // Recorded by this process itself, or a tab this root lists (even one whose session ended): this window's own.
        await Recorded("self-tab", ownerPid: Environment.ProcessId);
        Assert.Null(await Elsewhere((_, _) => true));
        var mine = await Recorded("my-tab");
        _liveTabs.Add(new TerminalTabStatusResponse("my-tab", DateTime.UtcNow, false, mine));
        Assert.Null(await Elsewhere(OtherWindow));

        var open = await _store.GetOpenSessionOwnersAsync([mine, Guid.NewGuid().ToString()], Ct);
        Assert.Equal(mine, Assert.Single(open).SessionId);
        Assert.Equal(OtherWindowPid, open[0].OwnerPid);
        Assert.Empty(await _store.GetOpenSessionOwnersAsync([], Ct));
    }

    [Fact]
    public async Task TheWorkerLimit_CountsWorkersAnotherWindowRuns_AndFreesTheirSlotWhenTheyEnd()
    {
        var boardId = await BoardWithManagerAsync();
        var busy = await _store.CreateCardAsync(_root, Card("Busy elsewhere", BoardWorkflowModes.Agent), Ct);
        var next = await _store.CreateCardAsync(_root, Card("Next", BoardWorkflowModes.Agent), Ct);
        var third = await _store.CreateCardAsync(_root, Card("Third", BoardWorkflowModes.Agent), Ct);
        var theirs = Guid.NewGuid().ToString();
        await _store.LinkSessionAsync(_root, busy.Id, theirs, "their-tab", "base:claude", "claude", "Worker", BoardSessionRecord.WorkerOrigin, Ct);
        await RecordSessionAsync(theirs);

        var refused = await Assert.ThrowsAsync<BoardConflictException>(() => Launcher(OtherWindow).LaunchAsync(_root, next.Id, null, Ct, intent: "worker"));
        Assert.Contains("at most 1 implementation worker at a time", refused.Message);
        Assert.Contains($"1 is running ({busy.DisplayId ?? busy.Key}) in another VibeRails window.", refused.Message);
        Assert.Null(refused.SessionId);
        Assert.Empty(_requests);
        var state = await Launcher(OtherWindow).GetBoardManagerLiveStateAsync(_root, boardId, Ct);
        Assert.Equal(theirs, Assert.Single(state.Workers).SessionId);
        Assert.Equal(1, state.WorkersElsewhere);
        Assert.Equal(1, (await new BoardManagerService(_store, Launcher(OtherWindow)).GetAsync(_root, boardId, Ct))!.RunningWorkers);

        // Two slots: one worker here and one there fill them, and only the one here can be opened.
        var saved = (await _store.GetBoardManagerAsync(_root, boardId, Ct))!;
        await _store.SaveBoardManagerAsync(_root, boardId, saved.Settings with { MaxWorkers = 2 }, saved.Revision, Ct);
        var here = (await Launcher(OtherWindow).LaunchAsync(_root, next.Id, null, Ct, intent: "worker"))!;
        var full = await Assert.ThrowsAsync<BoardConflictException>(() => Launcher(OtherWindow).LaunchAsync(_root, third.Id, null, Ct, intent: "worker"));
        Assert.Contains("and 2 are running", full.Message);
        Assert.Contains("1 of them in another VibeRails window", full.Message);
        Assert.Equal(here.SessionId, full.SessionId);

        // The other window's worker finished: its slot is free again.
        await EndSessionAsync(theirs);
        Assert.NotNull(await Launcher(OtherWindow).LaunchAsync(_root, third.Id, null, Ct, intent: "worker"));
        Assert.Equal(2, _requests.Count);
    }

    [Fact]
    public async Task AnotherWindowsManagerAndWorker_StillCount_AfterRunningForMoreThanADay()
    {
        // Review finding F1 (VIBE-262): age alone must never hide a live owner.
        var boardId = await BoardWithManagerAsync();
        var busy = await _store.CreateCardAsync(_root, Card("Long-running elsewhere", BoardWorkflowModes.Agent), Ct);
        var next = await _store.CreateCardAsync(_root, Card("Next", BoardWorkflowModes.Agent), Ct);
        var dayAndAHourAgo = DateTime.UtcNow.AddHours(-25);
        var manager = Guid.NewGuid().ToString();
        await _store.RecordBoardManagerSessionAsync(_root, boardId, manager, "their-manager-tab", "base:codex", "codex", Ct);
        await RecordSessionAsync(manager, startedUtc: dayAndAHourAgo);
        await BackdateLinkAsync("BoardManagerSessions", "StartedUTC", manager, dayAndAHourAgo);
        var worker = Guid.NewGuid().ToString();
        await _store.LinkSessionAsync(_root, busy.Id, worker, "their-worker-tab", "base:claude", "claude", "Worker", BoardSessionRecord.WorkerOrigin, Ct);
        await RecordSessionAsync(worker, startedUtc: dayAndAHourAgo);
        await BackdateLinkAsync("BoardCardSessions", "CreatedUTC", worker, dayAndAHourAgo);

        var state = await Launcher(OtherWindow).GetBoardManagerLiveStateAsync(_root, boardId, Ct);
        Assert.Equal(manager, state.Elsewhere?.SessionId);
        Assert.Equal(worker, Assert.Single(state.Workers).SessionId);
        Assert.Equal(1, state.WorkersElsewhere);
        var refused = await Assert.ThrowsAsync<BoardConflictException>(() => Launcher(OtherWindow).LaunchBoardManagerAsync(_root, boardId, Ct));
        Assert.Equal(BoardLaunchService.ManagerElsewhereMessage, refused.Message);
        var limited = await Assert.ThrowsAsync<BoardConflictException>(() => Launcher(OtherWindow).LaunchAsync(_root, next.Id, null, Ct, intent: "worker"));
        Assert.Contains("in another VibeRails window", limited.Message);
        Assert.Empty(_requests);
    }

    [Fact]
    public async Task AnotherWindowsWorker_IsFound_BeyondTheFirstBatchOfCandidates()
    {
        var boardId = await BoardWithManagerAsync();
        var card = await _store.CreateCardAsync(_root, Card("Many workers", BoardWorkflowModes.Agent), Ct);
        // More finished-but-unrecorded worker sessions than one state.db read takes, all newer than the live one.
        const int deadOwnerPid = 31337;
        for (var i = 0; i < BoardLaunchService.ElsewhereBatchSize + 5; i++)
        {
            var dead = Guid.NewGuid().ToString();
            await _store.LinkSessionAsync(_root, card.Id, dead, $"dead-tab-{i}", "base:claude", "claude", "Worker", BoardSessionRecord.WorkerOrigin, Ct);
            await RecordSessionAsync(dead, ownerPid: deadOwnerPid);
        }
        var live = Guid.NewGuid().ToString();
        await _store.LinkSessionAsync(_root, card.Id, live, "their-tab", "base:claude", "claude", "Worker", BoardSessionRecord.WorkerOrigin, Ct);
        await RecordSessionAsync(live, startedUtc: DateTime.UtcNow.AddDays(-3));
        await BackdateLinkAsync("BoardCardSessions", "CreatedUTC", live, DateTime.UtcNow.AddDays(-3));

        var state = await Launcher(OtherWindow).GetBoardManagerLiveStateAsync(_root, boardId, Ct);
        Assert.Equal(live, Assert.Single(state.Workers).SessionId);
        Assert.Equal(1, state.WorkersElsewhere);
    }

    public void Dispose()
    {
        SqliteConnection.ClearPool(new SqliteConnection(_connectionString));
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}
