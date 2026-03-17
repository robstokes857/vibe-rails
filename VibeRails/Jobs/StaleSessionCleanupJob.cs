using VibeRails.Interfaces;
using VibeRails.Services;

namespace VibeRails.Jobs;

public sealed class StaleSessionCleanupJob(
    ILogger<StaleSessionCleanupJob> logger,
    ISystemResourceService resources,
    IServiceScopeFactory scopeFactory) : JobBase(logger, resources)
{
    // Tab child processes are spawned with --parent-pid; only the root parent should clean up.
    private static readonly bool s_isTabChild =
        Environment.GetCommandLineArgs().Any(a =>
            a.StartsWith("--parent-pid", StringComparison.OrdinalIgnoreCase));

    protected override TimeSpan Interval => TimeSpan.FromMinutes(10);
    protected override JobPriority Priority => JobPriority.Lowest;

    protected override async Task ExecuteJob(CancellationToken cancellationToken)
    {
        if (s_isTabChild)
        {
            _logger.LogDebug("[StaleSessionCleanupJob] Running as tab child — skipping cleanup");
            return;
        }

        var cutoff = DateTime.UtcNow.AddMinutes(-5);
        using var scope = scopeFactory.CreateScope();
        var dbService = scope.ServiceProvider.GetRequiredService<IDbService>();

        var staleIds = await dbService.GetOpenSessionIdsAsync(cutoff, cancellationToken);

        if (staleIds.Count == 0)
            return;

        _logger.LogInformation("[StaleSessionCleanupJob] Closing {Count} stale session(s)", staleIds.Count);

        foreach (var id in staleIds)
            await dbService.CompleteSessionAsync(id, -1);
    }
}
