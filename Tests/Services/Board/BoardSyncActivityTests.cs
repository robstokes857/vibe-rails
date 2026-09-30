using System.Text.Json;
using Moq;
using Tests.Services.Terminal;
using VibeRails.Services.Board;
using VibeRails.Services.Board.Sync;
using Xunit;

namespace Tests.Services.Board;

public sealed class BoardSyncActivityTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TwoHundredEscapedSummariesUseOneOutcomeBatch_AndFitTheWireBudget()
    {
        var sessions = Enumerable.Range(0, 200).Select(i => new BoardSessionRecord(
            Guid.NewGuid().ToString(), "card", null, "base:codex", "codex", "Run " + i, "manual",
            DateTime.UtcNow.AddMinutes(-i))).ToList();
        var summary = new string('\u0001', 16000); // every character becomes six JSON bytes
        var outcomes = sessions.ToDictionary(s => s.SessionId, s => new BoardSessionOutcomeRecord(s.SessionId, DateTime.UtcNow, 0, summary));
        var store = Store(new(sessions, [], [], []), outcomes);

        var result = await BoardSyncActivity.CaptureAsync(store.Object, "project", "card", new(sessions, [], [], []), Ct);

        store.Verify(s => s.GetSyncSessionOutcomesAsync(It.Is<IReadOnlyList<string>>(ids => ids.Count == 200), Ct), Times.Once);
        store.Verify(s => s.FindSessionOutcomeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal(200, result.Sessions.Count);
        Assert.Equal(summary, result.Sessions[0].Summary);
        Assert.Null(result.Sessions[^1].Summary);
        Assert.Contains(result.Warnings, s => s.Contains("summaries"));
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(result, BoardSyncJsonContext.Default.BoardSyncActivityWire).Length < 7 * 1024 * 1024);
        Assert.All(outcomes.Values, o => Assert.Equal(summary, o.Summary));
    }

    [Fact]
    public async Task VerboseCommitMetadataIsReducedWithinTheEncodedLimit()
    {
        var commits = Enumerable.Range(0, 200).Select(i => new BoardCommitRecord("card", i.ToString("x40"),
            "Author", new string('\u263a', 16000), DateTime.UtcNow.AddMinutes(-i), DateTime.UtcNow)).ToList();
        var metadata = new BoardSyncActivityRecord([], commits, [], []);
        var store = Store(metadata);
        store.Setup(s => s.GetSyncCommitSnapshotAsync("project", "card", It.IsAny<string>(), 8 * 1024 * 1024, Ct))
            .ReturnsAsync((VibeRails.DTOs.SandboxDiffResponse?)null);

        var result = await BoardSyncActivity.CaptureAsync(store.Object, "project", "card", metadata, Ct);

        Assert.InRange(result.Commits.Count, 1, 199);
        Assert.Equal(commits[0].Sha, result.Commits[0].Sha);
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(result, BoardSyncJsonContext.Default.BoardSyncActivityWire).Length < 7 * 1024 * 1024);
        Assert.Contains(result.Warnings, s => s.Contains("transfer limit"));
    }

    [Fact]
    public async Task RecentSuccessSkipsDatabaseCapture_ThenExpires_AndManualRefreshBypassesIt()
    {
        var clock = new ManualTimeProvider();
        using var cache = new BoardSyncActivityCache(clock);
        var store = Store(new([], [], [], []));
        var client = Client();
        var link = Link();
        await BoardSyncActivity.RefreshAsync(store.Object, client.Object, cache, link, Ct);
        clock.Advance(TimeSpan.FromSeconds(59));
        await BoardSyncActivity.RefreshAsync(store.Object, client.Object, cache, link, Ct);
        store.Verify(s => s.GetSyncActivityAsync("project", "board", "card", Ct), Times.Once);

        clock.Advance(TimeSpan.FromSeconds(1));
        await BoardSyncActivity.RefreshAsync(store.Object, client.Object, cache, link, Ct);
        clock.Advance(TimeSpan.FromSeconds(5));
        await BoardSyncActivity.RefreshAsync(store.Object, client.Object, cache, link, Ct, force: true);
        store.Verify(s => s.GetSyncActivityAsync("project", "board", "card", Ct), Times.Exactly(3));
        client.Verify(c => c.PutActivityAsync("remote", "card", It.IsAny<BoardSyncActivityWire>(), Ct, "account"), Times.Once);

        // A destination/account change must never inherit another destination's successful check.
        await BoardSyncActivity.RefreshAsync(store.Object, client.Object, cache, link with { DestinationKey = "other-account" }, Ct);
        store.Verify(s => s.GetSyncActivityAsync("project", "board", "card", Ct), Times.Exactly(4));
        client.Verify(c => c.PutActivityAsync("remote", "card", It.IsAny<BoardSyncActivityWire>(), Ct, "other-account"), Times.Once);
    }

    [Fact]
    public async Task FailedAcknowledgementIsNeverCached()
    {
        using var cache = new BoardSyncActivityCache();
        var store = Store(new([], [], [], []));
        var client = Client();
        client.SetupSequence(c => c.PutActivityAsync("remote", "card", It.IsAny<BoardSyncActivityWire>(), Ct, "account"))
            .ReturnsAsync(new BoardSyncActivityAck(0, "card"))
            .ReturnsAsync(new BoardSyncActivityAck(1, "card"));
        await Assert.ThrowsAsync<BoardSyncClientException>(() => BoardSyncActivity.RefreshAsync(store.Object, client.Object, cache, Link(), Ct));
        await BoardSyncActivity.RefreshAsync(store.Object, client.Object, cache, Link(), Ct);
        store.Verify(s => s.GetSyncActivityAsync("project", "board", "card", Ct), Times.Exactly(2));
    }

    private static BoardSyncLinkRecord Link() => new("board", "remote", 0, true, null, null, null, default, default,
        ProjectPath: "project", DestinationKey: "account", ActivitySchema: 1);

    private static Mock<IBoardSyncClient> Client()
    {
        var client = new Mock<IBoardSyncClient>(MockBehavior.Strict);
        client.Setup(c => c.PutActivityAsync("remote", "card", It.IsAny<BoardSyncActivityWire>(), Ct, It.IsAny<string>()))
            .ReturnsAsync(new BoardSyncActivityAck(1, "card"));
        return client;
    }

    private static Mock<IBoardStore> Store(BoardSyncActivityRecord metadata,
        IReadOnlyDictionary<string, BoardSessionOutcomeRecord>? outcomes = null)
    {
        var store = new Mock<IBoardStore>(MockBehavior.Strict);
        store.Setup(s => s.GetSyncActivityCardIdsAsync("project", "board", It.IsAny<string?>(), 10, Ct)).ReturnsAsync(["card"]);
        store.Setup(s => s.GetSyncActivityAsync("project", "board", "card", Ct)).ReturnsAsync(metadata);
        store.Setup(s => s.GetAutomationSessionIdsAsync("project", It.IsAny<IReadOnlyList<string>>(), Ct)).ReturnsAsync(new HashSet<string>());
        store.Setup(s => s.GetSyncSessionOutcomesAsync(It.IsAny<IReadOnlyList<string>>(), Ct))
            .ReturnsAsync(outcomes ?? new Dictionary<string, BoardSessionOutcomeRecord>());
        store.Setup(s => s.SaveSyncLinkAsync(It.IsAny<BoardSyncLinkRecord>(), Ct)).ReturnsAsync((BoardSyncLinkRecord link, CancellationToken _) => link);
        return store;
    }
}
