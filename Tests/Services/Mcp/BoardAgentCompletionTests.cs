using Microsoft.Data.Sqlite;
using Moq;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services;
using VibeRails.Services.Board;
using VibeRails.Services.Mcp.Tools;
using Xunit;

namespace Tests.Services.Mcp;

public sealed class BoardAgentCompletionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "board-agent-" + Guid.NewGuid().ToString("N"));
    private readonly BoardStore store;
    private readonly Repository repository;
    private readonly JobStore jobs;
    private readonly BoardService service;
    private readonly Resolver resolver;
    private readonly BoardTool tool;
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public BoardAgentCompletionTests()
    {
        Directory.CreateDirectory(root);
        var state = $"Data Source={Path.Combine(root, "state.db")};Pooling=False";
        repository = new Repository(state);
        store = new BoardStore($"Data Source={Path.Combine(root, "board.db")};Pooling=False", state);
        jobs = new JobStore(state, store);
        service = new BoardService(store, Mock.Of<IBoardCommitService>(), new NullBoardLiveSessionProbe());
        resolver = new Resolver(root);
        tool = new BoardTool(service, resolver, store);
    }

    [Fact]
    public async Task CompletionIsDurableIdempotentScopedAndDoesNotEndTheProcess()
    {
        var card = await Card();
        var session = Guid.NewGuid().ToString();
        resolver.CurrentSessionId = session;
        Assert.StartsWith("FAIL:", await tool.CompleteBoardAgent("Ready", card: card.Key, cancellationToken: Ct));
        await repository.CreateSessionAsync(session, "codex", null, root, Environment.ProcessId);
        await store.LinkSessionAsync(root, card.Id, session, "tab", "base:codex", "codex", "Reviewer", "automation", Ct);
        await store.AddCommentAsync(root, card.Id, BoardAuthor.Agent("Reviewer", "codex", session), "Checking regressions", Ct);
        Assert.Contains("reported succeeded", await tool.CompleteBoardAgent("Tests passed", cancellationToken: Ct));
        Assert.Contains("Tests passed", await tool.CompleteBoardAgent("This retry cannot rewrite the result", "failed", cancellationToken: Ct));
        Assert.Null((await repository.GetSessionByIdAsync(session, Ct))!.EndedUTC);
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        {
            var retry = await service.CompleteAgentAsync(root.ToUpperInvariant(), card.Id, session, "failed", "case variant retry", Ct);
            Assert.Equal("Tests passed", retry!.Summary);
            Assert.Single(await store.GetAgentCompletionsAsync(root, card.Id, Ct));
        }

        var secondClient = new BoardTool(service, new Resolver(root), store);
        var status = await secondClient.GetBoardAgentStatus(card.Key, session, Ct);
        Assert.Contains("Agent reported succeeded", status);
        Assert.Contains("Tests passed", status);
        Assert.Contains("Checking regressions", status);
        Assert.Contains("liveness unknown", status); // A stdio host has no live PTY probe.
        await repository.CompleteSessionAsync(session, 7);
        Assert.Contains("exit code 7", await secondClient.GetBoardAgentStatus(card.Key, session, Ct));
        Assert.Empty(await store.GetAgentCompletionsAsync(Path.Combine(root, "other"), card.Id, Ct));
        Assert.Empty(await store.GetAgentSessionsAsync(Path.Combine(root, "other"), card.Id, session, Ct));
        Assert.StartsWith("FAIL:", await secondClient.CompleteBoardAgent("Cannot impersonate the agent", card: card.Key, cancellationToken: Ct));
        var other = await Card();
        Assert.StartsWith("FAIL:", await tool.GetBoardAgentStatus(other.Key, session, Ct));
    }

    [Fact]
    public async Task PollingSeesPendingQueuedAndFailedRunsBeforeARecordingExists()
    {
        var card = await Card();
        var job = await jobs.CreateJobAsync(new("Review", root, LLM.NotSet, null, "", null, true, [],
            Actions: [new(null, JobActionKind.Script, ScriptPath: "check.py", ScriptRuntime: JobScriptRuntime.Python)]), Ct);
        var review = (await store.GetColumnsAsync(root, Ct)).Single(c => c.Name == "Review");
        await store.SaveLaneAutomationAsync(root, review.Id, [job.Id], 0, Ct);
        await store.MoveCardAsync(root, card.Id, review.Id, null, Ct);
        Assert.Contains("Pending Automation: Review", await tool.GetBoardAgentStatus(card.Key, cancellationToken: Ct));

        var runId = (await jobs.EnqueueBoardCardRunAsync(root, job.Id, card.Key, Ct))!;
        var queued = await tool.GetBoardAgentStatus(card.Key, cancellationToken: Ct);
        Assert.Contains($"Run {runId}", queued);
        Assert.Contains("Queued", queued);
        await jobs.StartRunAsync(runId, Environment.ProcessId, Ct);
        await jobs.CompleteRunAsync(runId, JobRunStatus.Failed, 1, "Worker failed before launch", Ct);
        var failed = await tool.GetBoardAgentStatus(card.Key, cancellationToken: Ct);
        Assert.Contains("Failed", failed);
        Assert.Contains("Worker failed before launch", failed);
        Assert.Empty(await store.GetAgentRunsAsync(Path.Combine(root, "foreign"), card.Id, Ct));
        var other = await Card();
        Assert.DoesNotContain(runId, await tool.GetBoardAgentStatus(other.Key, cancellationToken: Ct));
    }

    [Theory]
    [InlineData("", "succeeded")]
    [InlineData("valid", "maybe")]
    public async Task InvalidCompletionDoesNotPersist(string summary, string outcome)
    {
        var card = await Card();
        resolver.CurrentSessionId = Guid.NewGuid().ToString();
        await store.LinkSessionAsync(root, card.Id, resolver.CurrentSessionId, null, "", "codex", "Agent", "mcp", Ct);
        Assert.StartsWith("FAIL:", await tool.CompleteBoardAgent(summary, outcome, card.Key, Ct));
        Assert.Empty(await store.GetAgentCompletionsAsync(root, card.Id, Ct));
    }

    [Fact]
    public async Task PollingBoundsSessionsAndLatestUpdateAndCanFindAnOlderSession()
    {
        var card = await Card();
        var ids = new List<string>();
        for (var i = 0; i < 12; i++)
        {
            var id = Guid.NewGuid().ToString();
            ids.Add(id);
            await store.LinkSessionAsync(root, card.Id, id, null, "", "codex", "Agent", "mcp", Ct);
            await store.AddNoteAsync(root, card.Id, BoardAuthor.Agent("Agent", "codex", id), new string('x', 5000), Ct);
        }
        var statuses = await store.GetAgentSessionsAsync(root, card.Id, cancellationToken: Ct);
        Assert.Equal(10, statuses.Count);
        Assert.All(statuses, s => Assert.Equal(600, s.LastUpdate!.Length));
        Assert.Single(await store.GetAgentSessionsAsync(root, card.Id, ids[0], Ct));
        Assert.Contains(ids[0], await tool.GetBoardAgentStatus(card.Key, ids[0], Ct));
    }

    private async Task<BoardCardRecord> Card()
    {
        await service.GetColumnsAsync(root, Ct);
        return await store.CreateCardAsync(root, new(null, "Work", "Task", null, "medium", null, [], false), Ct);
    }

    private sealed class Resolver(string project) : IBoardProjectResolver
    {
        public string GitWorkingDirectory => project;
        public string? CurrentSessionId { get; set; }
        public Task<string> ResolveAsync(CancellationToken cancellationToken = default) => Task.FromResult(project);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(root, true);
    }
}
