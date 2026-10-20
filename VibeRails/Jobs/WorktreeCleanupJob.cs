using VibeRails.Services;
using VibeRails.Services.Workspaces;

namespace VibeRails.Jobs;

/// <summary>Checks absolute worktree expiry while a root backend is open; no separate host.</summary>
public sealed class WorktreeCleanupJob(
    ILogger<WorktreeCleanupJob> logger,
    ISystemResourceService resources,
    IManagedWorktreeService worktrees) : JobBase(logger, resources)
{
    protected override TimeSpan Interval => TimeSpan.FromMinutes(10);
    protected override Task ExecuteJob(CancellationToken cancellationToken) => worktrees.CleanupExpiredAsync(cancellationToken);
}
