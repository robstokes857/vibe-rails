using VibeRails.Services;
using VibeRails.Services.BertV2;

namespace VibeRails.Jobs;

/// <summary>Repairs derived search indexes. Source reconciliation belongs to <see cref="BertEmbeddingBackfillJob"/> (VB-2GUR8-187).</summary>
public sealed class SearchIndexMaintenanceJob(ILogger<SearchIndexMaintenanceJob> logger,
    ISystemResourceService resources, ISearchIndexStore store) : JobBase(logger, resources)
{
    protected override TimeSpan Interval => TimeSpan.FromMinutes(15);
    protected override Task ExecuteJob(CancellationToken cancellationToken)
    {
        if (store.Repair()) Serilog.Log.Warning("[SearchIndex] Repaired the derived search index");
        return Task.CompletedTask;
    }
}
