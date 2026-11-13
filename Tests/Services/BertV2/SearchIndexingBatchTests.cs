using Moq;
using VibeRails.Services.BertV2;
using VibeRails.Services.Board;
using Xunit;

namespace Tests.Services.BertV2;

/// <summary>VIBE-321 #2: one claim takes several items; whatever the budget leaves unprocessed is released.</summary>
public sealed class SearchIndexingBatchTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ABatchClaimStopsAtTheBudget_ReleasesTheRest_AndStillServesEveryKind()
    {
        var store = Store(out var released, out var completed);
        var embedder = new Mock<IBertV2BgeEmbedder>();
        embedder.Setup(e => e.GenerateEmbedding(It.IsAny<string>())).Returns(() => { Thread.Sleep(80); return new float[384]; });
        var indexing = new SearchIndexingService(store.Object, Mock.Of<IBoardStore>(),
            () => throw new InvalidOperationException("no source is claimed"), () => embedder.Object, TimeSpan.FromMilliseconds(40));

        Assert.True(await indexing.RunBatchAsync(Ct));

        // The first kind's claim took four items, ran one, and gave the other three back at once.
        store.Verify(s => s.ClaimChunks("board", 4, SearchIndexVersions.Model), Times.Once);
        Assert.Equal(["board-1", "board-2", "board-3"], released.Order());
        // Past the budget, every later kind still gets one item in the same round.
        store.Verify(s => s.ClaimChunks("input", 1, SearchIndexVersions.Model), Times.Once);
        store.Verify(s => s.ClaimChunks("session", 1, SearchIndexVersions.Model), Times.Once);
        Assert.Equal(["board-0", "input-0", "session-0"], completed.Order());
    }

    [Fact]
    public async Task WithinTheBudgetEveryClaimedItemRunsAndNothingIsReleased()
    {
        var store = Store(out var released, out var completed);
        var embedder = new Mock<IBertV2BgeEmbedder>();
        embedder.Setup(e => e.GenerateEmbedding(It.IsAny<string>())).Returns(new float[384]);
        var indexing = new SearchIndexingService(store.Object, Mock.Of<IBoardStore>(),
            () => throw new InvalidOperationException("no source is claimed"), () => embedder.Object);

        await indexing.RunBatchAsync(Ct, perKind: 1);

        Assert.Empty(released);
        Assert.Equal(12, completed.Count);
    }

    private static Mock<ISearchIndexStore> Store(out List<string> released, out List<string> completed)
    {
        var releasedItems = released = [];
        var completedItems = completed = [];
        var store = new Mock<ISearchIndexStore>(MockBehavior.Loose);
        store.Setup(s => s.ReconcileAsync(It.IsAny<IBoardStore>(), It.IsAny<int>(), It.IsAny<CancellationToken>(),
            It.IsAny<TimeSpan?>(), It.IsAny<TimeSpan?>())).ReturnsAsync(new SearchReconcileResult(0, 0, false));
        store.Setup(s => s.ClaimSources(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>())).Returns([]);
        store.Setup(s => s.ClaimChunks(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>()))
            .Returns((string kind, int count, string _) => Enumerable.Range(0, count)
                .Select(i => new SearchWork($"{kind}-{i}", kind, "text", "", "hash", "lease")).ToList());
        store.Setup(s => s.Renew(It.IsAny<SearchWork>(), It.IsAny<bool>())).Returns(true);
        store.Setup(s => s.CompleteChunk(It.IsAny<SearchWork>(), It.IsAny<float[]>(), It.IsAny<string>()))
            .Callback((SearchWork work, float[] _, string _) => { lock (completedItems) completedItems.Add(work.Id); });
        store.Setup(s => s.Release(It.IsAny<SearchWork>(), false))
            .Callback((SearchWork work, bool _) => { lock (releasedItems) releasedItems.Add(work.Id); });
        return store;
    }
}
