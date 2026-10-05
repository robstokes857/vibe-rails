using VibeRails.Services;
using VibeRails.Services.BertV2;
using VibeRails.Services.Board;

namespace VibeRails.Jobs;

/// <summary>Reconciles source changes and repairs derived indexes.</summary>
public sealed class SearchIndexMaintenanceJob(ILogger<SearchIndexMaintenanceJob> logger,
    ISystemResourceService resources, ISearchIndexStore store, IBoardStore board) : JobBase(logger, resources)
{
    protected override TimeSpan Interval => TimeSpan.FromMinutes(3);
    protected override async Task ExecuteJob(CancellationToken cancellationToken)
    {
        await store.ReconcileAsync(board, 25, cancellationToken);
        if (store.Repair()) Serilog.Log.Warning("[SearchIndex] Repaired the derived search index");
    }
}
