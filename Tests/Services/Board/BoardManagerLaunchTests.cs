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

    private BoardLaunchService Launcher() =>
        new(_store, _repository.Object, _tabs.Object, new BoardContextEstimator(
            new BoardService(_store, new Mock<IBoardCommitService>().Object, new NullBoardLiveSessionProbe()), _store, _repository.Object),
            launchSettings: () => new Settings { CreateVibeStoryTracking = _nudge });

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

    [Fact]
    public async Task TheWorkerLimit_HoldsAcrossTheBoardsManagedCards_ButNotReviewsPlannersOrOwnManagers()
    {
        var boardId = await BoardWithManagerAsync();
        var busy = await _store.CreateCardAsync(_root, Card("Busy", BoardWorkflowModes.Agent), Ct);
        var next = await _store.CreateCardAsync(_root, Card("Next", BoardWorkflowModes.Agent), Ct);
        var own = await _store.CreateCardAsync(_root, Card("Own manager", BoardWorkflowModes.Agent, "base:codex"), Ct);
        var running = await LiveWorkerAsync(busy);

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

    public void Dispose()
    {
        SqliteConnection.ClearPool(new SqliteConnection(_connectionString));
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}
