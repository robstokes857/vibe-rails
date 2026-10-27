using Microsoft.Data.Sqlite;
using Moq;
using VibeRails.DTOs;
using VibeRails.Services.Board;
using VibeRails.Services.Board.PostProcessing;
using Xunit;

namespace Tests.Services.Board;

public sealed class BoardGhostTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "board-ghost-events-" + Guid.NewGuid().ToString("N"));
    private readonly BoardStore store;
    private readonly Check check = new();
    private readonly LinkCheck links = new();
    private readonly BoardGhostService ghosts;
    private CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Sha = "abcdef0123456789abcdef0123456789abcdef01";

    public BoardGhostTests()
    {
        Directory.CreateDirectory(root);
        var connection = $"Data Source={Path.Combine(root, "fixture.db")};Pooling=False";
        store = new(connection, connection);
        ghosts = new(store, new GhostCardPostProcessor(check, links));
    }

    private async Task<BoardCardRecord> Ghost(string session = "worker")
    {
        await store.EnsureDefaultColumnsAsync(root, Ct);
        var lanes = await store.GetColumnsAsync(root, Ct);
        var card = await store.CreateCardAsync(root, new(null, "Task", "", null, "medium", null, [], false), Ct);
        await store.LinkSessionAsync(root, card.Id, session, null, "base:codex", "codex", "Codex", "launch", Ct);
        await store.MoveCardAsync(root, card.Id, lanes[^1].Id, null, Ct);
        var candidate = (await store.GetPostProcessingCandidatesAsync(DateTime.UtcNow.AddMinutes(1), 100, Ct)).Single(c => c.CardId == card.Id);
        Assert.True(await store.CompletePostProcessingAsync(candidate, new(BoardGhostReasons.NoCode), Ct));
        return (await store.FindCardAsync(root, card.Id, Ct))!;
    }

    private BoardService Service()
    {
        var commits = new Mock<IBoardCommitService>();
        commits.Setup(c => c.DescribeAsync(root, Sha, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BoardCommitInfo(Sha, "Author", "Work", DateTime.UtcNow));
        commits.Setup(c => c.GetDiffAsync(root, Sha, It.IsAny<CancellationToken>())).ReturnsAsync(new BoardCommitDiff([], 0));
        return new(store, commits.Object, new NullBoardLiveSessionProbe(), ghosts: ghosts);
    }

    [Fact]
    public async Task LinkingLandedCommitRechecksOnlyItsExactAffectedGhostCards()
    {
        var first = await Ghost();
        var second = await Ghost();
        var unrelated = await Ghost("another-session");
        await Service().LinkCommitAsync(root, first.Id, Sha, Ct, sessionId: "worker");
        Assert.Null((await store.FindCardAsync(root, first.Id, Ct))!.GhostReason);
        Assert.Null((await store.FindCardAsync(root, second.Id, Ct))!.GhostReason);
        Assert.Equal(BoardGhostReasons.NoCode, (await store.FindCardAsync(root, unrelated.Id, Ct))!.GhostReason);
        Assert.Equal(2, check.Calls);
        Assert.Empty(await store.GetPostProcessingCandidatesAsync(DateTime.UtcNow.AddDays(300), 100, Ct));
    }

    [Fact]
    public async Task LinkingUnlandedCommitKeepsGhostUntilExplicitRecheck()
    {
        var card = await Ghost();
        check.Result = BoardCommitLanding.NotLanded;
        await Service().LinkCommitAsync(root, card.Id, Sha, Ct);
        Assert.Equal(BoardGhostReasons.NotLanded, (await store.FindCardAsync(root, card.Id, Ct))!.GhostReason);
        check.Result = BoardCommitLanding.Landed;
        Assert.Equal("clear", (await ghosts.RecheckAsync(root, card.Id, Ct))!.Status);
        Assert.Null((await store.FindCardAsync(root, card.Id, Ct))!.GhostReason);
        Assert.Single(await store.GetCommitsAsync(root, card.Id, Ct));
        Assert.Empty(await store.GetPostProcessingCandidatesAsync(DateTime.UtcNow.AddDays(300), 100, Ct));
    }

    [Fact]
    public async Task OpenPrKeepsGhostAndRelinkingMergedPrClearsIt()
    {
        var card = await Ghost();
        links.Result = BoardCommitLanding.NotLanded;
        const string url = "https://github.com/org/repo/pull/42";
        await Service().LinkCodeAsync(root, card.Id, url, Ct);
        Assert.Equal(BoardGhostReasons.NotLanded, (await store.FindCardAsync(root, card.Id, Ct))!.GhostReason);
        links.Result = BoardCommitLanding.Landed;
        await Service().LinkCodeAsync(root, card.Id, url, Ct);
        Assert.Null((await store.FindCardAsync(root, card.Id, Ct))!.GhostReason);
        Assert.Single(await store.GetCodeLinksAsync(root, card.Id, Ct));
    }

    [Fact]
    public async Task UnavailableCheckKeepsGhostAndDoesNotQueueAnotherPass()
    {
        var card = await Ghost();
        links.Result = BoardCommitLanding.Unverifiable;
        await Service().LinkCodeAsync(root, card.Id, "https://github.com/org/repo/pull/42", Ct);
        var result = await ghosts.RecheckAsync(root, card.Id, Ct);
        Assert.Equal("unverified", result!.Status);
        Assert.Equal(BoardGhostReasons.NoCode, result.GhostReason);
        Assert.Empty(await store.GetPostProcessingCandidatesAsync(DateTime.UtcNow.AddDays(300), 100, Ct));
    }

    [Fact]
    public async Task MissingGitHubAccessDoesNotStartRepeatedChecksOfACompletedCard()
    {
        await store.EnsureDefaultColumnsAsync(root, Ct);
        var lanes = await store.GetColumnsAsync(root, Ct);
        var card = await store.CreateCardAsync(root, new(lanes[^1].Id, "One check", "", null, "medium", null, [], false), Ct);
        await store.LinkSessionAsync(root, card.Id, "one-check", null, "base:codex", "codex", "Codex", "launch", Ct);
        await store.AddCodeLinkAsync(root, card.Id, "https://github.com/org/repo/pull/42", Ct);
        links.Result = BoardCommitLanding.Unverifiable;
        var processor = new BoardCardPostProcessingService(store, [new GhostCardPostProcessor(check, links)]);
        var pass = await processor.RunOnceAsync(DateTime.UtcNow.AddHours(1), Ct);
        Assert.Equal(1, pass.Processed);
        Assert.Equal(0, pass.Deferred);
        Assert.Equal(0, (await processor.RunOnceAsync(DateTime.UtcNow.AddDays(150), Ct)).Candidates);
        Assert.Equal(1, links.Calls);
    }

    [Fact]
    public async Task ConcurrentDismissalCannotBeReplacedByAnOldCheck()
    {
        var card = await Ghost();
        await store.AddCommitAsync(root, card.Id, Sha, "Author", "Work", DateTime.UtcNow, new SandboxDiffResponse([], 0), Ct);
        check.BeforeReturn = async () => { await store.UpdateCardAsync(root, card.Id, new BoardCardPatch(ClearGhost: true), Ct); };
        check.Result = BoardCommitLanding.NotLanded;
        Assert.Equal("stale", (await ghosts.RecheckAsync(root, card.Id, Ct))!.Status);
        Assert.Null((await store.FindCardAsync(root, card.Id, Ct))!.GhostReason);
    }

    [Fact]
    public async Task StaleReplyReportsTheMarkerTheKeptStateCarries()
    {
        var card = await Ghost();
        await store.AddCommitAsync(root, card.Id, Sha, "Author", "Work", DateTime.UtcNow, new SandboxDiffResponse([], 0), Ct);
        check.BeforeReturn = async () => { await store.UpdateCardAsync(root, card.Id, new BoardCardPatch(Title: "Edited meanwhile"), Ct); };
        check.Result = BoardCommitLanding.Landed;
        var reply = (await ghosts.RecheckAsync(root, card.Id, Ct))!;
        Assert.Equal("stale", reply.Status);
        Assert.Equal(BoardGhostReasons.NoCode, reply.GhostReason);
        Assert.Equal(BoardGhostReasons.NoCode, (await store.FindCardAsync(root, card.Id, Ct))!.GhostReason);
    }

    [Fact]
    public async Task ASnapshotStaysCurrentWhateverSpellingTheStoredTimestampUses()
    {
        var card = await Ghost();
        await store.AddCommitAsync(root, card.Id, Sha, "Author", "Work", DateTime.UtcNow, new SandboxDiffResponse([], 0), Ct);
        // The same instant, written the way a non-round-trip writer might.
        await using (var connection = new SqliteConnection($"Data Source={Path.Combine(root, "fixture.db")};Pooling=False"))
        {
            await connection.OpenAsync(Ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE BoardCards SET UpdatedUTC = '2026-10-27T14:10:20+00:00' WHERE Id = $id;";
            command.Parameters.AddWithValue("$id", card.Id);
            await command.ExecuteNonQueryAsync(Ct);
        }
        check.Result = BoardCommitLanding.Landed;
        Assert.Equal("clear", (await ghosts.RecheckAsync(root, card.Id, Ct))!.Status);
        Assert.Null((await store.FindCardAsync(root, card.Id, Ct))!.GhostReason);
    }

    [Fact]
    public async Task GhostFilterIncludesUnloadedCardsAndCorrectCounts()
    {
        var first = await Ghost();
        var second = await Ghost("other");
        await store.UpdateCardAsync(root, first.Id, new BoardCardPatch(ClearGhost: true), Ct);
        var page = await store.GetCardsPageAsync(root, new(PageSize: 1, Ghost: true), Ct);
        Assert.Equal(2, page.TotalCount);
        Assert.Equal(1, page.FilteredCount);
        Assert.Equal(second.Id, Assert.Single(page.Cards).Id);
        var nonGhost = await store.GetCardsPageAsync(root, new(PageSize: 1, Ghost: false), Ct);
        Assert.Equal(first.Id, Assert.Single(nonGhost.Cards).Id);
    }

    private sealed class Check : IBoardDefaultBranchCheck
    {
        public BoardCommitLanding Result = BoardCommitLanding.Landed;
        public int Calls;
        public Func<Task>? BeforeReturn;
        public async Task<BoardCommitLanding> CheckAsync(string projectPath, IReadOnlyList<string> shas, CancellationToken cancellationToken)
        {
            Calls++;
            if (BeforeReturn is not null) await BeforeReturn();
            return Result;
        }
    }
    private sealed class LinkCheck : IBoardCodeLinkLandingCheck
    {
        public BoardCommitLanding Result = BoardCommitLanding.Landed;
        public int Calls;
        public Task<BoardCommitLanding> CheckAsync(string projectPath, IReadOnlyList<BoardCodeLink> links, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(Result);
        }
    }
    public void Dispose() { SqliteConnection.ClearAllPools(); try { Directory.Delete(root, true); } catch (IOException) { } }
}
