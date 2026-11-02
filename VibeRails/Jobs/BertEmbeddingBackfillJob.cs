using VibeRails.Services;
using VibeRails.Services.BertV2;

namespace VibeRails.Jobs;

/// <summary>Existing root-only index worker, shared fairly by Board and captured history.</summary>
/// <remarks>
/// An idle root checks the canonical sources every five minutes: a sweep that finds nothing changed is
/// pure overhead on search.db, and until 2026-10-07 it ran every five seconds in every root around the
/// clock (VB-2GUR8-187). A root with a backlog (first start, a model or chunk version change, many new
/// captures) follows up after a few seconds until a batch reports nothing left.
/// <para>
/// A large backlog waits for an idle machine (VIBE-172, <see cref="SearchBackfillGate"/>). While it
/// waits, the job still sweeps the sources, so lexical search keeps finding new work, and it rechecks
/// the machine every <see cref="HoldRecheck"/>.
/// </para>
/// </remarks>
public sealed class BertEmbeddingBackfillJob : JobBase
{
    internal static readonly TimeSpan Idle = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan FollowUp = TimeSpan.FromSeconds(5);
    /// <summary>How soon a held backlog checks again whether the machine has gone idle.</summary>
    internal static readonly TimeSpan HoldRecheck = TimeSpan.FromSeconds(30);

    private readonly ISystemResourceService _resources;
    private readonly SearchIndexingService _indexing;
    private readonly TimeProvider _clock;
    private readonly SearchBackfillGate _gate = new();
    private SearchBackfillDecision _lastDecision = SearchBackfillDecision.SmallBacklog;
    private State _state = State.Backlog; // catch up shortly after start instead of waiting a full interval

    public BertEmbeddingBackfillJob(ILogger<BertEmbeddingBackfillJob> logger,
        ISystemResourceService resources, SearchIndexingService indexing)
        : this(logger, resources, indexing, TimeProvider.System) { }

    internal BertEmbeddingBackfillJob(ILogger<BertEmbeddingBackfillJob> logger,
        ISystemResourceService resources, SearchIndexingService indexing, TimeProvider clock) : base(logger, resources)
    {
        _resources = resources;
        _indexing = indexing;
        _clock = clock;
    }

    protected override TimeSpan Interval => Idle;
    protected override TimeSpan NextDelay => _state switch
    {
        State.Backlog => FollowUp,
        State.Holding => HoldRecheck,
        _ => Idle,
    };
    protected override JobPriority Priority => JobPriority.Med;
    protected override Task ExecuteJob(CancellationToken cancellationToken) => TickAsync(cancellationToken);

    /// <summary>One tick: a batch of inference, or only a sweep while a large backlog waits for an idle machine.</summary>
    internal async Task TickAsync(CancellationToken cancellationToken)
    {
        var machine = _resources.Current;
        var pending = _indexing.CountPendingWork();
        var now = _clock.GetUtcNow();
        var decision = _gate.Decide(pending, machine, now);
        if (decision != _lastDecision)
        {
            Serilog.Log.Information(
                "[SearchIndex] Backfill {Previous} -> {Decision}: pending={Pending} otherCpu={OtherCpu:F0} quietFor={QuietFor} userIdle={UserIdle} waited={Waited}",
                _lastDecision, decision, pending, machine.OtherCpuPercent, machine.QuietFor, machine.UserIdleFor,
                now - (_gate.LargeSince ?? now));
            _lastDecision = decision;
        }
        if (decision == SearchBackfillDecision.Hold)
        {
            _state = State.Holding;
            await _indexing.SweepAsync(cancellationToken);
            return;
        }
        _state = await _indexing.RunBatchAsync(cancellationToken) ? State.Backlog : State.Idle;
    }

    private enum State { Idle, Backlog, Holding }
}
