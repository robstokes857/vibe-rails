using VibeRails.Data.Abstractions;
using VibeRails.Services;

namespace VibeRails.Jobs;

public sealed class SearchIndexMaintenanceJob(
    ILogger<SearchIndexMaintenanceJob> logger,
    ISystemResourceService resources,
    ISearchIndexMaintenanceStore store) : JobBase(logger, resources)
{
    // Drain queued writes every tick; judge the whole index about once an hour. The consistency
    // check reads every indexed rowid, which is cheap at thousands of prompts but not free.
    private const int ConsistencyCheckEveryTicks = 20;
    private int _ticks;

    protected override TimeSpan Interval => TimeSpan.FromMinutes(3);

    protected override async Task ExecuteJob(CancellationToken cancellationToken)
    {
        var completed = await store.RepairPendingAsync(100, cancellationToken);
        if (completed > 0)
            Serilog.Log.Information("[Job:SearchIndexMaintenanceJob] Completed {Count} queued search-index writes.", completed);

        if (++_ticks % ConsistencyCheckEveryTicks != 1)
            return;
        if (await store.RepairIndexIfInconsistentAsync(cancellationToken))
            Serilog.Log.Warning("[Job:SearchIndexMaintenanceJob] Rebuilt the prompt search index: it had drifted from its content table, which happens when an older VibeRails build writes to it directly.");
    }
}
