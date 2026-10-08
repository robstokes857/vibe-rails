using VibeRails.Services;
using VibeRails.Services.BertV2;

namespace VibeRails.Jobs;

/// <summary>Existing root-only index worker, shared fairly by Board and captured history.</summary>
/// <remarks>
/// An idle root checks the canonical sources every five minutes: a sweep that finds nothing changed is
/// pure overhead on search.db, and until 2026-10-07 it ran every five seconds in every root around the
/// clock (VB-2GUR8-187). A root with a backlog (first start, a model or chunk version change, many new
/// captures) follows up after a few seconds until a batch reports nothing left.
/// </remarks>
public sealed class BertEmbeddingBackfillJob(ILogger<BertEmbeddingBackfillJob> logger,
    ISystemResourceService resources, SearchIndexingService indexing) : JobBase(logger, resources)
{
    internal static readonly TimeSpan Idle = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan FollowUp = TimeSpan.FromSeconds(5);
    private bool _backlog = true; // catch up shortly after start instead of waiting a full interval

    protected override TimeSpan Interval => Idle;
    protected override TimeSpan NextDelay => _backlog ? FollowUp : Idle;
    protected override JobPriority Priority => JobPriority.Med;
    protected override async Task ExecuteJob(CancellationToken cancellationToken) =>
        _backlog = await indexing.RunBatchAsync(cancellationToken);
}
