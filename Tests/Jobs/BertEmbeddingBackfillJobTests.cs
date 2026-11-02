using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using VibeRails.Jobs;
using VibeRails.Services;
using VibeRails.Services.BertV2;
using VibeRails.Services.Board;
using Xunit;

namespace Tests.Jobs;

/// <summary>The backfill job's small/large backlog handling and wait cap (VIBE-172).</summary>
public sealed class BertEmbeddingBackfillJobTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ANewCardIsIndexedRightAwayOnABusyMachine()
    {
        var fixture = new Fixture(pending: 4, SearchBackfillGateTests.Busy);

        await fixture.Job.TickAsync(Ct);

        fixture.AssertRanInference();
    }

    [Fact]
    public async Task AFullReEmbedOnlySweepsWhileTheMachineIsBusy()
    {
        var fixture = new Fixture(pending: 18_000, SearchBackfillGateTests.Busy);

        await fixture.Job.TickAsync(Ct);

        // The sweep keeps lexical search current; no source is chunked and no chunk is embedded.
        fixture.Store.Verify(s => s.ReconcileAsync(It.IsAny<IBoardStore>(), It.IsAny<int>(), It.IsAny<CancellationToken>(),
            It.IsAny<TimeSpan?>(), It.IsAny<TimeSpan?>()), Times.Once);
        fixture.Store.Verify(s => s.ClaimSources(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>()), Times.Never);
        fixture.Store.Verify(s => s.ClaimChunks(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task AFullReEmbedRunsOnceTheMachineIsIdle()
    {
        var fixture = new Fixture(pending: 18_000, SearchBackfillGateTests.Busy);
        await fixture.Job.TickAsync(Ct);

        fixture.Machine = SearchBackfillGateTests.Quiet;
        fixture.Clock.Now = Start.AddMinutes(3);
        await fixture.Job.TickAsync(Ct);

        fixture.AssertRanInference();
    }

    [Fact]
    public async Task AFullReEmbedStartsAnywayAfterTheWaitCapOnAMachineThatIsAlwaysBusy()
    {
        var fixture = new Fixture(pending: 18_000, SearchBackfillGateTests.Busy);
        await fixture.Job.TickAsync(Ct);
        fixture.Clock.Now = Start + SearchBackfillGate.MaxWait - TimeSpan.FromSeconds(30);
        await fixture.Job.TickAsync(Ct);
        fixture.Store.Verify(s => s.ClaimSources(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>()), Times.Never);

        fixture.Clock.Now = Start + SearchBackfillGate.MaxWait;
        await fixture.Job.TickAsync(Ct);

        fixture.AssertRanInference();
    }

    private sealed class Fixture
    {
        public readonly Mock<ISearchIndexStore> Store = new(MockBehavior.Loose);
        public readonly ManualClock Clock = new(Start);
        public ResourceSnapshot Machine;
        public readonly BertEmbeddingBackfillJob Job;

        public Fixture(int pending, ResourceSnapshot machine)
        {
            Machine = machine;
            Store.Setup(s => s.CountPendingWork()).Returns(pending);
            Store.Setup(s => s.ReconcileAsync(It.IsAny<IBoardStore>(), It.IsAny<int>(), It.IsAny<CancellationToken>(),
                It.IsAny<TimeSpan?>(), It.IsAny<TimeSpan?>())).ReturnsAsync(new SearchReconcileResult(0, 0, false));
            Store.Setup(s => s.ClaimSources(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>())).Returns([]);
            Store.Setup(s => s.ClaimChunks(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>())).Returns([]);
            var resources = new Mock<ISystemResourceService>();
            resources.SetupGet(r => r.Current).Returns(() => Machine);
            var indexing = new SearchIndexingService(Store.Object, Mock.Of<IBoardStore>(),
                () => throw new InvalidOperationException("no work is claimed"),
                () => throw new InvalidOperationException("no work is claimed"));
            Job = new BertEmbeddingBackfillJob(NullLogger<BertEmbeddingBackfillJob>.Instance, resources.Object, indexing, Clock);
        }

        public void AssertRanInference() =>
            Store.Verify(s => s.ClaimSources(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>()), Times.AtLeastOnce);
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
