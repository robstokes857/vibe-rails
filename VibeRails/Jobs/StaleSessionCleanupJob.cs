using System.Diagnostics;
using VibeRails.Interfaces;
using VibeRails.Services;

namespace VibeRails.Jobs;

public sealed class StaleSessionCleanupJob(
    ILogger<StaleSessionCleanupJob> logger,
    ISystemResourceService resources,
    IServiceScopeFactory scopeFactory) : JobBase(logger, resources)
{
    private static volatile bool s_didComplete;

    protected override TimeSpan Interval => TimeSpan.FromMinutes(10);
    protected override JobPriority Priority => JobPriority.Lowest;

    protected override async Task ExecuteJob(CancellationToken cancellationToken)
    {
        if (s_didComplete)
            return;

        var procName = Process.GetCurrentProcess().ProcessName;
        if (Process.GetProcessesByName(procName).Length > 1)
        {
            _logger.LogDebug("[StaleSessionCleanupJob] Multiple instances detected — skipping cleanup");
            return;
        }

        var cutoff = DateTime.UtcNow.AddMinutes(-5);
        using var scope = scopeFactory.CreateScope();
        var dbService = scope.ServiceProvider.GetRequiredService<IDbService>();

        var staleIds = await dbService.GetOpenSessionIdsAsync(cutoff, cancellationToken);

        s_didComplete = true;

        if (staleIds.Count == 0)
            return;

        _logger.LogInformation("[StaleSessionCleanupJob] Closing {Count} stale session(s)", staleIds.Count);

        foreach (var id in staleIds)
            await dbService.CompleteSessionAsync(id, -1);
    }
}
