using System.Diagnostics;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services;
using VibeRails.Services.Terminal;
using VibeRails.Services.Integrations.VibeCodeRemote;

namespace VibeRails.Jobs;

public sealed class StaleSessionCleanupJob(
    ILogger<StaleSessionCleanupJob> logger,
    ISystemResourceService resources,
    IServiceScopeFactory scopeFactory,
    IRemoteStateService remoteStateService) : JobBase(logger, resources)
{
    protected override TimeSpan Interval => TimeSpan.FromMinutes(10);
    protected override JobPriority Priority => JobPriority.Lowest;

    protected override async Task ExecuteJob(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var trackedCutoff = now.AddMinutes(-5);
        var untrackedCutoff = now.AddHours(-1);
        using var scope = scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IRepository>();

        var staleSessions = await repository.GetOpenSessionCleanupCandidatesAsync(trackedCutoff, untrackedCutoff, cancellationToken);

        if (staleSessions.Count == 0)
            return;

        var orphanedSessions = new List<OpenSessionCleanupCandidate>();

        foreach (var session in staleSessions)
        {
            // No owner PID means the session was created by a pre-ownership-tracking build
            // and can't be liveness-checked; if it survived the untracked cutoff, reap it.
            if (!session.OwnerPid.HasValue)
            {
                orphanedSessions.Add(session);
                continue;
            }

            if (IsProcessAlive(session.OwnerPid.Value))
                continue;

            orphanedSessions.Add(session);
        }

        if (orphanedSessions.Count == 0)
            return;

        _logger.LogInformation("[StaleSessionCleanupJob] Closing {Count} stale orphaned session(s)", orphanedSessions.Count);

        foreach (var session in orphanedSessions)
        {
            await repository.CompleteSessionAsync(session.SessionId, -1);
            await remoteStateService.DeregisterTerminalAsync(session.SessionId);
        }
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }
}
