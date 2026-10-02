using VibeRails.Services;
using VibeRails.Services.Backups;

namespace VibeRails.Jobs;

/// <summary>Complete-backup drain in the existing resource-aware root-host job lifecycle.</summary>
public sealed class CompleteBackupJob(ILogger<CompleteBackupJob> logger, ISystemResourceService resources, CompleteBackupService backups)
    : JobBase(logger, resources)
{
    protected override TimeSpan Interval => TimeSpan.FromSeconds(15);
    protected override async Task ExecuteJob(CancellationToken cancellationToken) => await backups.TickAsync(cancellationToken);
}
