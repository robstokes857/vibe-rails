using VibeRails.Data.Abstractions;
using VibeRails.Services;
using VibeRails.Services.Diagnostics;
using VibeRails.Services.Integrations.VibeCodeRemote;
using VibeRails.Utils;

namespace VibeRails.Jobs;

/// <summary>
/// Schedules small retention batches only while automatic backup is opted in AND retention itself
/// is switched on. Two flags on purpose: "back my data up" must never by itself mean "and then
/// delete it locally" -- deletion is the one storage action with no undo.
/// </summary>
public sealed class DataRetentionJob(
    ILogger<DataRetentionJob> logger,
    ISystemResourceService resources,
    IDataRetentionStore store,
    IFeatureLog featureLog) : JobBase(logger, resources)
{
    protected override TimeSpan Interval => TimeSpan.FromMinutes(5);
    protected override JobPriority Priority => JobPriority.Low;

    protected override async Task ExecuteJob(CancellationToken cancellationToken)
    {
        var settings = Config.LoadFresh();
        if (!settings.DataExportOptIn || !settings.DataRetentionEnabled)
            return;
        // Reuse the archive drain's machine-wide lock. Retention cannot race preparation or
        // acknowledgment, and multiple root backends cannot prune the same data simultaneously.
        using var lease = CrossProcessFileLock.TryAcquire(CrossProcessFileLock.BesideStateDatabase(
            ParserConfigs.GetStatePath(), SessionDataExportService.LockFileName));
        if (lease is null)
            return;
        var result = await store.PruneAsync(DateTime.UtcNow, cancellationToken);
        if (result.SessionsDeleted == 0 && result.StateRowsDeleted == 0
            && result.ProxyExchangesDeleted == 0 && result.EmbeddingRowsDeleted == 0)
            return;
        var message = $"Local retention removed {result.SessionsDeleted} backed-up sessions, {result.StateRowsDeleted} session records, "
            + $"{result.ProxyExchangesDeleted} proxy exchanges, and {result.EmbeddingRowsDeleted} embedding rows.";
        logger.LogInformation("{Message}", message);
        featureLog.Write("data-retention", "pruned", message, subject: "Local data", status: "succeeded");
    }
}
