using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services;
using VibeRails.Services.Board;
using VibeRails.Services.Integrations.VibeCodeRemote;

namespace VibeRails.Jobs;

/// <summary>
/// Closes open session rows whose recorded owner process is no longer running them. The owner is
/// still running a session only while its PID is alive, readable and started before the session did
/// (<see cref="SessionOwnerProcess"/>). A PID the operating system has since handed to another
/// process is not the owner, including another user's process that cannot be inspected at all:
/// after a reboot, macOS hands low PIDs to root-owned daemons, and a bare "has it exited" check
/// called those daemons live forever, so the rows never closed.
/// </summary>
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

            if (SessionOwnerProcess.IsRunning(session.OwnerPid.Value, session.StartedUTC))
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
}
