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

    /// <summary>
    /// Review f4151632 F1: through the scheduled JobBase path, a machine kept saturated by other processes
    /// gets one bounded wait, then a continuous drain, with source sweeps throughout.
    /// </summary>
    [Fact]
    public async Task UnderSustainedMachinePressureAFullReEmbedStartsAtTheWaitCapAndKeepsDraining()
    {
        var fixture = new Fixture(pending: 18_000, SearchBackfillGateTests.Busy, cpuPressure: true, working: true);
        DateTimeOffset? first = null;
        var batches = 0;
        while (fixture.Clock.Now < Start + SearchBackfillGate.MaxWait + TimeSpan.FromMinutes(5))
        {
            var before = fixture.Embedded;
            var delay = await fixture.Job.RunTickAsync(Ct);
            if (fixture.Embedded > before) { first ??= fixture.Clock.Now; batches++; }
            fixture.Clock.Now += delay;
        }

        Assert.Equal(Start + SearchBackfillGate.MaxWait, first);
        // Every five-second follow-up after the cap runs a batch: five minutes is sixty of them.
        Assert.InRange(batches, 59, 61);
        // The sixty held ticks, every 30 seconds, still reconciled the sources.
        fixture.Store.Verify(s => s.ReconcileAsync(It.IsAny<IBoardStore>(), It.IsAny<int>(), It.IsAny<CancellationToken>(),
            It.IsAny<TimeSpan?>(), It.IsAny<TimeSpan?>()), Times.AtLeast(60 + batches));
    }

    [Fact]
    public async Task ANewCardIsIndexedOnTheFirstScheduledTickUnderSustainedCpuPressure()
    {
        var fixture = new Fixture(pending: 4, SearchBackfillGateTests.Busy, cpuPressure: true);

        await fixture.Job.RunTickAsync(Ct);

        fixture.AssertRanInference();
    }

    [Fact]
    public async Task MemoryPressureStillDefersTheBackfill()
    {
        var fixture = new Fixture(pending: 4, SearchBackfillGateTests.Busy, memoryPressure: true);

        Assert.Equal(BertEmbeddingBackfillJob.Idle, await fixture.Job.RunTickAsync(Ct));

        fixture.Store.Verify(s => s.ReconcileAsync(It.IsAny<IBoardStore>(), It.IsAny<int>(), It.IsAny<CancellationToken>(),
            It.IsAny<TimeSpan?>(), It.IsAny<TimeSpan?>()), Times.Never);
    }

    private sealed class Fixture
    {
        public readonly Mock<ISearchIndexStore> Store = new(MockBehavior.Loose);
        public readonly ManualClock Clock = new(Start);
        public ResourceSnapshot Machine;
        public readonly BertEmbeddingBackfillJob Job;
        private int _embedded;
        public int Embedded => Volatile.Read(ref _embedded);

        /// <param name="working">Every chunk claim returns an item, so each batch finishes work and asks for a follow-up.</param>
        public Fixture(int pending, ResourceSnapshot machine, bool cpuPressure = false, bool memoryPressure = false, bool working = false)
        {
            Machine = machine;
            Store.Setup(s => s.CountPendingWork()).Returns(pending);
            Store.Setup(s => s.ReconcileAsync(It.IsAny<IBoardStore>(), It.IsAny<int>(), It.IsAny<CancellationToken>(),
                It.IsAny<TimeSpan?>(), It.IsAny<TimeSpan?>())).ReturnsAsync(new SearchReconcileResult(0, 0, false));
            Store.Setup(s => s.ClaimSources(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>())).Returns([]);
            if (working)
                Store.Setup(s => s.ClaimChunks(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>()))
                    .Returns((string kind, int _, string _) => new List<SearchWork> { new("chunk-" + kind, kind, "text", "", "hash", "lease") });
            else
                Store.Setup(s => s.ClaimChunks(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>())).Returns([]);
            Store.Setup(s => s.Renew(It.IsAny<SearchWork>(), It.IsAny<bool>())).Returns(true);
            Store.Setup(s => s.CompleteChunk(It.IsAny<SearchWork>(), It.IsAny<float[]>(), It.IsAny<string>()))
                .Callback(() => Interlocked.Increment(ref _embedded));
            var embedder = new Mock<IBertV2BgeEmbedder>();
            embedder.Setup(e => e.GenerateEmbedding(It.IsAny<string>())).Returns(new float[384]);
            var resources = new Mock<ISystemResourceService>();
            resources.SetupGet(r => r.Current).Returns(() => Machine);
            resources.SetupGet(r => r.IsCpuUnderPressure).Returns(cpuPressure);
            resources.SetupGet(r => r.IsMemoryUnderPressure).Returns(memoryPressure);
            var indexing = new SearchIndexingService(Store.Object, Mock.Of<IBoardStore>(),
                () => throw new InvalidOperationException("no source is claimed"), () => embedder.Object);
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
