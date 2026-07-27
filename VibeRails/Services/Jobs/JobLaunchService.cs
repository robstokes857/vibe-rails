using Serilog;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services.LlmClis;
using VibeRails.Utils;

namespace VibeRails.Services.Jobs;

public interface IJobLaunchService
{
    /// <summary>Spawns terminals for every launchable queued run. Returns how many were launched.</summary>
    Task<int> LaunchQueuedRunsAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Opens a real OS terminal window per queued Job run, running the Job's Environment/Worker
/// EXACTLY as the user configured it on the Environment screen — the same command the Env screen's
/// launch button produces.
///
/// This is deliberately NOT an automation-specific invocation. There is no non-interactive rewrite,
/// no auto-approval flag injection, and no filtering of the env's saved arguments: whatever the user
/// chose is what runs, including permission-skipping flags. A Job is an Env on a timer, nothing more.
///
/// The spawned process (<c>vb --env … --job-run &lt;id&gt;</c>) owns the run from there: it claims it
/// under its own PID, records its own session, honours cancellation, enforces its own deadline, and
/// writes its own outcome. This service never waits on it.
/// </summary>
public sealed class JobLaunchService(
    IJobStore store,
    IEnvironmentLaunchService environmentLaunchService) : IJobLaunchService
{
    /// <summary>
    /// Ceiling on simultaneously open job terminals across the whole machine. The per-job overlap
    /// guard in <see cref="JobStore"/> stops one job stacking windows; this stops many jobs coming
    /// due at once from burying the desktop. Each window is a full vb host plus a CLI process tree,
    /// so this is a real resource bound, not just a cosmetic one.
    /// </summary>
    public const int MaxConcurrentJobTerminals = 3;

    public async Task<int> LaunchQueuedRunsAsync(CancellationToken cancellationToken = default)
    {
        var launchable = await store.GetLaunchableRunsAsync(cancellationToken);
        if (launchable.Count == 0)
            return 0;

        // Terminals already open from earlier ticks count against the cap too — otherwise every
        // tick would happily open another full set on top of the ones still running.
        var alreadyOpen = await store.CountRunningRunsAsync(cancellationToken);
        var launched = 0;

        foreach (var run in launchable)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            if (alreadyOpen + launched >= MaxConcurrentJobTerminals)
            {
                Log.Information(
                    "[Jobs] Launch cap reached ({Cap}, {Open} already open); {Remaining} run(s) stay queued for the next tick",
                    MaxConcurrentJobTerminals, alreadyOpen, launchable.Count - launched);
                break;
            }

            // Claim before doing anything observable. The shared scheduler lease prevents normal
            // contention; this row claim is the handoff/crash safety net.
            if (!await store.TryMarkLaunchedAsync(run.Id, cancellationToken))
                continue;

            if (await LaunchOneAsync(run, cancellationToken))
                launched++;
        }

        return launched;
    }

    private async Task<bool> LaunchOneAsync(JobRunRecord run, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(run.ProjectPath))
        {
            await FailAsync(run, $"The project directory no longer exists: {run.ProjectPath}");
            return false;
        }

        var vbArgs = BuildVbArgs(run);

        LaunchResultSnapshot result;
        try
        {
            // This is the exact application pipeline behind POST /api/v1/cli/launch/{cli}. The
            // empty Args array is the same request the Environments screen sends; only vb's private
            // run-bookkeeping flags differ.
            var launch = await environmentLaunchService.LaunchAsync(
                run.Llm,
                new LaunchCliRequest(run.ProjectPath, run.EnvironmentName, []),
                run.ProjectPath,
                vbArgs,
                keepTerminalOpen: false,
                environmentId: run.EnvironmentId,
                cancellationToken: cancellationToken);
            result = new LaunchResultSnapshot(launch.Success, launch.Message);
        }
        catch (Exception ex)
        {
            await FailAsync(run, $"Could not open a terminal for this Automation: {ex.Message}");
            return false;
        }

        if (!result.Success)
        {
            await FailAsync(run, result.Message);
            return false;
        }

        Log.Information(
            "[Jobs] Launched run {RunId} for job '{JobName}' using worker '{Worker}' in {ProjectPath}",
            run.Id, run.JobName, run.EnvironmentName, run.ProjectPath);
        return true;
    }

    /// <summary>
    /// vb's own flags for the spawned process. <c>--job-run</c> makes it self-bookkeeping and tags
    /// its session so the run stays out of Chat History. <c>--max-runtime</c> is only present when
    /// the user opted into a timeout; without it the run lives until the CLI exits or the window
    /// is closed.
    /// </summary>
    public static string[] BuildVbArgs(JobRunRecord run)
    {
        var args = new List<string> { "--job-run", run.Id };
        if (run.TimeoutMinutes is > 0)
        {
            args.Add("--max-runtime");
            args.Add(run.TimeoutMinutes.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        return args.ToArray();
    }

    private async Task FailAsync(JobRunRecord run, string message)
    {
        Log.Warning("[Jobs] Run {RunId} could not be launched: {Message}", run.Id, message);
        await store.CompleteRunAsync(run.Id, JobRunStatus.Failed, null, message, CancellationToken.None);
    }

    private readonly record struct LaunchResultSnapshot(bool Success, string Message);
}
