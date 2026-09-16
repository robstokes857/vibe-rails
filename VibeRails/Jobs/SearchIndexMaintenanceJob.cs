using VibeRails.Data.Abstractions;
using VibeRails.Services;

namespace VibeRails.Jobs;

public sealed class SearchIndexMaintenanceJob(
    ILogger<SearchIndexMaintenanceJob> logger,
    ISystemResourceService resources,
    ISearchIndexMaintenanceStore store) : JobBase(logger, resources)
{
    protected override TimeSpan Interval => TimeSpan.FromMinutes(3);

    protected override async Task ExecuteJob(CancellationToken cancellationToken)
    {
        var completed = await store.RepairPendingAsync(100, cancellationToken);
        if (completed > 0)
            Serilog.Log.Information("[Job:SearchIndexMaintenanceJob] Completed {Count} queued search-index writes.", completed);
    }
}
