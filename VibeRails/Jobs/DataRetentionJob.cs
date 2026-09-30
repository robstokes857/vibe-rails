using VibeRails.Data.Abstractions;
using VibeRails.Services;
using VibeRails.Services.Diagnostics;
using VibeRails.Services.Integrations.VibeCodeRemote;
using VibeRails.Utils;

namespace VibeRails.Jobs;

/// <summary>
/// Deletes local copies of sessions that have already been uploaded, after their retention
/// window. Always on. Open sessions and anything without an upload acknowledgement are kept.
/// </summary>
public sealed class DataRetentionJob(
    ILogger<DataRetentionJob> logger,
    ISystemResourceService resources,
    IDataRetentionStore store,
    IFeatureLog featureLog,
    IServiceScopeFactory scopeFactory) : JobBase(logger, resources)
{
    private readonly ProxyRetentionSchedule _proxySchedule = new();

    protected override TimeSpan Interval => TimeSpan.FromMinutes(5);
    protected override JobPriority Priority => JobPriority.Low;

    protected override async Task ExecuteJob(CancellationToken cancellationToken)
    {
        // Reuse the archive drain's machine-wide lock. Retention cannot race preparation or
        // acknowledgment, and multiple root backends cannot prune the same data simultaneously.
        using var lease = CrossProcessFileLock.TryAcquire(CrossProcessFileLock.BesideStateDatabase(
            ParserConfigs.GetStatePath(), SessionDataExportService.LockFileName));
        if (lease is null)
            return;
        var nowUtc = DateTime.UtcNow;
        // IGlobalCache is scoped; the schedule keeps this process's in-memory copy of the deferral.
        using var scope = scopeFactory.CreateScope();
        var cache = scope.ServiceProvider.GetRequiredService<IGlobalCache>();
        var includeProxy = await _proxySchedule.IsDueAsync(cache, nowUtc);
        var result = await store.PruneAsync(nowUtc, includeProxy, cancellationToken);
        if (includeProxy && result.ProxyPruneComplete && result.ProxyExchangesDeleted == 0)
            await _proxySchedule.DeferAsync(cache, nowUtc);
        if (result.SessionsDeleted == 0 && result.StateRowsDeleted == 0
            && result.ProxyExchangesDeleted == 0 && result.EmbeddingRowsDeleted == 0)
            return;
        var message = $"Local retention removed {result.SessionsDeleted} backed-up sessions, {result.StateRowsDeleted} session records, "
            + $"{result.ProxyExchangesDeleted} proxy exchanges, and {result.EmbeddingRowsDeleted} embedding rows.";
        logger.LogInformation("{Message}", message);
        featureLog.Write("data-retention", "pruned", message, subject: "Local data", status: "succeeded");
    }
}
