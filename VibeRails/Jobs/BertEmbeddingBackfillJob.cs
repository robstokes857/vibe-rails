using VibeRails.Services;
using VibeRails.Services.BertV2;

namespace VibeRails.Jobs;

/// <summary>Existing root-only index worker, shared fairly by Board and captured history.</summary>
public sealed class BertEmbeddingBackfillJob(ILogger<BertEmbeddingBackfillJob> logger,
    ISystemResourceService resources, SearchIndexingService indexing) : JobBase(logger, resources)
{
    protected override TimeSpan Interval => TimeSpan.FromSeconds(5);
    protected override JobPriority Priority => JobPriority.Med;
    protected override Task ExecuteJob(CancellationToken cancellationToken) => indexing.RunBatchAsync(cancellationToken);
}
