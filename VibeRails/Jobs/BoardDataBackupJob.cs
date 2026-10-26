using VibeRails.Services;
using VibeRails.Services.Board.Backup;

namespace VibeRails.Jobs;

/// <summary>Runs the independent PostgreSQL Board archive only while a root backend is open.</summary>
public sealed class BoardDataBackupJob(ILogger<BoardDataBackupJob> logger, ISystemResourceService resources,
    BoardDataBackupService backups) : JobBase(logger, resources)
{
    protected override TimeSpan Interval => TimeSpan.FromMinutes(1);
    protected override Task ExecuteJob(CancellationToken cancellationToken) => backups.TickAsync(cancellationToken);
}
