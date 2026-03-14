using VibeRails.Services;

namespace VibeRails.Jobs;

public sealed class UpdateCheckJob(
    ILogger<UpdateCheckJob> logger,
    ISystemResourceService resources,
    UpdateService updateService) : JobBase(logger, resources)
{
    protected override TimeSpan Interval => TimeSpan.FromMinutes(30);
    protected override JobPriority Priority => JobPriority.Low;

    protected override async Task ExecuteJob(CancellationToken cancellationToken)
    {
        var update = await updateService.CheckForUpdateAsync(cancellationToken);
        if (update?.UpdateAvailable == true)
        {
            _logger.LogInformation(
                "[UpdateCheckJob] Update available: {Latest} (current: {Current}) — {Url}",
                update.LatestVersion, update.CurrentVersion, update.ReleaseNotesUrl);
        }
    }
}
