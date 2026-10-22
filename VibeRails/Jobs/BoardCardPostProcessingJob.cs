using VibeRails.Services;
using VibeRails.Services.Board.PostProcessing;

namespace VibeRails.Jobs;

/// <summary>
/// Runs the Board's run-once card post-processing (VIBE-185) while a root backend is open; no
/// separate host. Every open root runs it; the store's conditional write applies each card once.
/// </summary>
public sealed class BoardCardPostProcessingJob(
    ILogger<BoardCardPostProcessingJob> logger,
    ISystemResourceService resources,
    BoardCardPostProcessingService postProcessing) : JobBase(logger, resources)
{
    protected override TimeSpan Interval => TimeSpan.FromMinutes(5);

    protected override async Task ExecuteJob(CancellationToken cancellationToken)
    {
        var pass = await postProcessing.RunOnceAsync(DateTime.UtcNow, cancellationToken);
        if (pass.Candidates > 0)
            Serilog.Log.Information(
                "[BoardPostProcessing] candidates={Candidates} processed={Processed} ghosts={Ghosts} deferred={Deferred} skipped={Skipped}",
                pass.Candidates, pass.Processed, pass.Ghosts, pass.Deferred, pass.Skipped);
    }
}
