using Microsoft.Data.Sqlite;
using Moq;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services.Board;
using VibeRails.Services.Terminal;
using Xunit;

namespace Tests.Services.Board;

public sealed class BoardLaunchConcurrencyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"viberails-board-launch-{Guid.NewGuid():N}");
    private readonly string _connectionString;
    private readonly BoardStore _store;
    private readonly Mock<ITerminalTabHostService> _tabs = new(MockBehavior.Strict);
    private readonly Mock<IRepository> _repository = new(MockBehavior.Strict);
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public BoardLaunchConcurrencyTests()
    {
        Directory.CreateDirectory(_root);
        _connectionString = $"Data Source={Path.Combine(_root, "state.db")};Mode=ReadWriteCreate;Cache=Shared";
        _store = new BoardStore(_connectionString, _connectionString);
        _tabs.SetupGet(t => t.MaxTabs).Returns(8);
        _tabs.Setup(t => t.ListTabsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _tabs.Setup(t => t.CreateTabAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new TerminalTabStatusResponse("tab-1", DateTime.UtcNow, false));
        _tabs.Setup(t => t.DeleteTabAsync("tab-1", It.IsAny<CancellationToken>())).ReturnsAsync(true);
    }

    [Fact]
    public async Task ConcurrentLaunchesAcrossServiceInstancesOnlyStartOneCli()
    {
        var card = await CreateCardAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<TerminalStatusResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _tabs.Setup(t => t.StartSessionAsync("tab-1", It.IsAny<StartTerminalRequest>(), It.IsAny<CancellationToken>()))
            .Callback(() => entered.TrySetResult()).Returns(finish.Task);
        var first = Launcher().LaunchAsync(_root, card.Id, null, Ct);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        try
        {
            await Assert.ThrowsAsync<BoardConflictException>(() =>
                Launcher().LaunchAsync(_root, card.Key, null, Ct));
        }
        finally { finish.TrySetResult(new TerminalStatusResponse(true, Guid.NewGuid().ToString(), "codex", _root)); }
        Assert.NotNull(await first);
        _tabs.Verify(t => t.StartSessionAsync(It.IsAny<string>(), It.IsAny<StartTerminalRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Single((await _store.GetCardDetailAsync(_root, card.Id, Ct))!.Sessions);
    }

    [Fact]
    public async Task FailedLaunchReleasesReservationSoRetryCanStart()
    {
        var card = await CreateCardAsync();
        _tabs.SetupSequence(t => t.StartSessionAsync("tab-1", It.IsAny<StartTerminalRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("failed startup"))
            .ReturnsAsync(new TerminalStatusResponse(true, Guid.NewGuid().ToString(), "codex", _root));
        var service = Launcher();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.LaunchAsync(_root, card.Id, null, Ct));
        Assert.NotNull(await service.LaunchAsync(_root, card.Key, null, Ct));
        _tabs.Verify(t => t.StartSessionAsync(It.IsAny<string>(), It.IsAny<StartTerminalRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LaunchPromptCountsActivityBeforeLinkingItsOwnSession(bool withPriorActivity)
    {
        var card = await CreateCardAsync();
        if (withPriorActivity)
        {
            await _store.AddCommentAsync(_root, card.Id, BoardAuthor.User(), "Continue the earlier fix", Ct);
            await _store.AddNoteAsync(_root, card.Id, BoardAuthor.Agent("codex", "codex", null), "Investigated the failing test", Ct);
            await _store.LinkSessionAsync(_root, card.Id, Guid.NewGuid().ToString(), null, "base:codex", "codex", "Earlier work", BoardSessionRecord.LaunchOrigin, Ct);
        }
        StartTerminalRequest? request = null;
        _tabs.Setup(t => t.StartSessionAsync("tab-1", It.IsAny<StartTerminalRequest>(), It.IsAny<CancellationToken>()))
            .Callback<string, StartTerminalRequest, CancellationToken>((_, value, _) => request = value)
            .ReturnsAsync(new TerminalStatusResponse(true, Guid.NewGuid().ToString(), "codex", _root));

        await Launcher().LaunchAsync(_root, card.Id, null, Ct);

        var count = withPriorActivity ? 1 : 0;
        Assert.NotNull(request);
        Assert.Contains($"Card activity before this session: {count * 2} comments · 0 agent notes · {count} earlier sessions · 0 linked commits.", request.InitialPrompt);
        Assert.Contains("otherwise begin with the repository instructions and task", request.InitialPrompt);
        Assert.DoesNotContain("Begin now by reading the card", request.InitialPrompt);
        Assert.Equal(count + 1, (await _store.GetCardDetailAsync(_root, card.Id, Ct))!.Sessions.Count);

        // VB-63: every launch records how much context the card put in front of the agent, and the
        // Card Log carries it as a `context` change so History and Board sync see the same number.
        var sample = await _store.GetLatestContextSampleAsync(_root, card.Id, Ct);
        Assert.NotNull(sample);
        Assert.Equal("work", sample!.Intent);
        Assert.Equal("codex", sample.Cli);
        Assert.NotNull(sample.SessionId);
        Assert.Equal(ContextTokenEstimator.Estimate(request.InitialPrompt), sample.PromptTokens);
        Assert.True(sample.CardReadTokens > 0);
        Assert.True(sample.Tokens >= sample.PromptTokens + sample.CardReadTokens);
        Assert.Contains("\"sources\"", sample.BreakdownJson);
        var history = await _store.GetCardHistoryAsync(_root, card.Id, Ct);
        var entry = Assert.Single(history, h => h.Changes?.Contains("\"context\"", StringComparison.Ordinal) == true);
        Assert.Equal(BoardCommentKinds.Change, entry.Kind);
        Assert.StartsWith("Agent launch context ≈", entry.Body);
        Assert.Equal(BoardAuthor.SystemKind, entry.Author.Kind);
    }

    private async Task<BoardCardRecord> CreateCardAsync()
    {
        await _store.EnsureDefaultColumnsAsync(_root, Ct);
        return await _store.CreateCardAsync(_root, new(null, "Concurrent card", "Scope", "base:codex", "medium", null, [], false), Ct);
    }

    // A real estimator over the same store: the launch records its context sample like production does.
    private BoardLaunchService Launcher() =>
        new(_store, _repository.Object, _tabs.Object, new BoardContextEstimator(
            new BoardService(_store, new Mock<IBoardCommitService>().Object, new NullBoardLiveSessionProbe()), _store, _repository.Object));

    [Fact]
    public async Task LiveAutomationDoesNotBlockStartWork_ButTheCardsOwnAgentDoes()
    {
        // VB-6Q8ZS-68 follow-up: the Review lane's run and the CLI it spawned stay linked to the
        // card, so with the launched agent's tab gone Start work must still launch a new agent
        // instead of refusing with "already running". Only JobRuns.SessionId marks the reviewer.
        var card = await CreateCardAsync();
        const string run = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
        const string reviewer = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";
        const string agent = "cccccccc-cccc-4ccc-8ccc-cccccccccccc";
        await _store.LinkSessionAsync(_root, card.Id, run, "run-tab", "", "shell", "Automation: codex_code_review", BoardSessionRecord.AutomationOrigin, Ct);
        await _store.LinkSessionAsync(_root, card.Id, reviewer, "reviewer-tab", "", "codex", "codex_code_review", BoardSessionRecord.McpOrigin, Ct);
        await using (var connection = new SqliteConnection(_connectionString))
        {
            await connection.OpenAsync(Ct);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE JobRuns(ProjectPath TEXT, SessionId TEXT, TerminalSessionId TEXT);
                INSERT INTO JobRuns VALUES ($project, $reviewer, $run);
                """;
            command.Parameters.AddWithValue("$project", _root);
            command.Parameters.AddWithValue("$reviewer", reviewer);
            command.Parameters.AddWithValue("$run", run);
            await command.ExecuteNonQueryAsync(Ct);
        }
        var liveTabs = new List<TerminalTabStatusResponse>
        {
            new("run-tab", DateTime.UtcNow, true, run),
            new("reviewer-tab", DateTime.UtcNow, true, reviewer)
        };
        _tabs.Setup(t => t.ListTabsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => (IReadOnlyList<TerminalTabStatusResponse>)liveTabs.ToList());
        _tabs.Setup(t => t.StartSessionAsync("tab-1", It.IsAny<StartTerminalRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TerminalStatusResponse(true, agent, "codex", _root));

        Assert.NotNull(await Launcher().LaunchAsync(_root, card.Id, null, Ct));
        _tabs.Verify(t => t.StartSessionAsync(It.IsAny<string>(), It.IsAny<StartTerminalRequest>(), It.IsAny<CancellationToken>()), Times.Once);

        // The launched agent is linked with the launch origin and now live: a second launch is refused.
        liveTabs.Add(new("tab-1", DateTime.UtcNow, true, agent));
        await Assert.ThrowsAsync<BoardConflictException>(() => Launcher().LaunchAsync(_root, card.Key, null, Ct));
        _tabs.Verify(t => t.StartSessionAsync(It.IsAny<string>(), It.IsAny<StartTerminalRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CardDeletedDuringStartupClosesTheUnlinkedTerminal()
    {
        var card = await CreateCardAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<TerminalStatusResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _tabs.Setup(t => t.StartSessionAsync("tab-1", It.IsAny<StartTerminalRequest>(), It.IsAny<CancellationToken>()))
            .Callback(() => entered.TrySetResult()).Returns(finish.Task);
        var launch = Launcher().LaunchAsync(_root, card.Id, null, Ct);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        try { Assert.True(await _store.DeleteCardAsync(_root, card.Id, Ct)); }
        finally { finish.TrySetResult(new TerminalStatusResponse(true, Guid.NewGuid().ToString(), "codex", _root)); }
        await Assert.ThrowsAsync<BoardValidationException>(() => launch);
        _tabs.Verify(t => t.DeleteTabAsync("tab-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    public void Dispose()
    {
        SqliteConnection.ClearPool(new SqliteConnection(_connectionString));
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}
