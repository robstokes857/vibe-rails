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
    IRepository repository,
    ILaunchLLMService launchService) : IJobLaunchService
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

            // Claim the right to spawn before doing anything observable, so a concurrent tick or
            // the dashboard scheduler can't open a second window for the same run.
            if (!await store.TryMarkLaunchedAsync(run.Id, cancellationToken))
                continue;

            if (await LaunchOneAsync(run, cancellationToken))
                launched++;
        }

        return launched;
    }

    private async Task<bool> LaunchOneAsync(JobRunRecord run, CancellationToken cancellationToken)
    {
        var environment = await ResolveEnvironmentAsync(run, cancellationToken);
        if (environment is null)
        {
            var label = string.IsNullOrWhiteSpace(run.EnvironmentName) ? string.Empty : $" '{run.EnvironmentName}'";
            await FailAsync(run, $"This Job's Environment / Worker{label} no longer exists.");
            return false;
        }

        if (!Directory.Exists(run.ProjectPath))
        {
            await FailAsync(run, $"The project directory no longer exists: {run.ProjectPath}");
            return false;
        }

        string[] cliArgs;
        try
        {
            cliArgs = BuildCliArgs(environment);
        }
        catch (Exception ex)
        {
            // A malformed CustomArgs string is the user's to fix; report it against the run rather
            // than letting it take down the tick for every other job.
            await FailAsync(run, $"The worker's custom arguments could not be parsed: {ex.Message}");
            return false;
        }

        var vbArgs = BuildVbArgs(run);

        LaunchResultSnapshot result;
        try
        {
            // keepTerminalOpen: false — the window exists to show this one run. The CLI now runs as
            // the PTY's own process, so it exits when the CLI does; leaving a shell behind would
            // strand one idle terminal window per run for the user to close by hand.
            var launch = launchService.LaunchInTerminal(
                environment.LLM,
                environment.CustomName,
                run.ProjectPath,
                cliArgs,
                vbArgs,
                keepTerminalOpen: false);
            result = new LaunchResultSnapshot(launch.Success, launch.Message);
        }
        catch (Exception ex)
        {
            await FailAsync(run, $"Could not open a terminal for this Job: {ex.Message}");
            return false;
        }

        if (!result.Success)
        {
            await FailAsync(run, result.Message);
            return false;
        }

        Log.Information(
            "[Jobs] Launched run {RunId} for job '{JobName}' using worker '{Worker}' in {ProjectPath}",
            run.Id, run.JobName, environment.CustomName, run.ProjectPath);
        return true;
    }

    /// <summary>
    /// The env's saved arguments, verbatim, plus its initial message using the same per-CLI
    /// interactive convention every other launch path uses
    /// (<see cref="LlmPromptArgvBuilder.AppendInitialPrompt"/>). Nothing is added, removed, or
    /// reordered — this is the whole point of the Jobs redesign.
    /// </summary>
    public static string[] BuildCliArgs(LLM_Environment environment)
    {
        var args = new List<string>();
        if (!string.IsNullOrWhiteSpace(environment.CustomArgs))
        {
            args.AddRange(ShellArgSanitizer.ParseAndValidate(environment.CustomArgs));
        }
        LlmPromptArgvBuilder.AppendInitialPrompt(args, environment.LLM, environment.CustomPrompt);
        return args.ToArray();
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

    /// <summary>
    /// Resolves by stable EnvironmentId first: EnvironmentName on the run is only a snapshot taken
    /// at enqueue time, so renaming a worker between enqueue and launch must not make the run fail
    /// as nonexistent. Runs queued before the id was recorded fall back to name + LLM.
    /// </summary>
    private async Task<LLM_Environment?> ResolveEnvironmentAsync(JobRunRecord run, CancellationToken cancellationToken)
    {
        if (run.EnvironmentId is int environmentId)
        {
            var byId = await repository.GetEnvironmentByIdAsync(environmentId, cancellationToken);
            if (byId is not null)
                return byId;
        }

        if (string.IsNullOrWhiteSpace(run.EnvironmentName))
            return null;

        return await repository.GetEnvironmentByNameAndLlmAsync(run.EnvironmentName, run.Llm, cancellationToken);
    }

    private async Task FailAsync(JobRunRecord run, string message)
    {
        Log.Warning("[Jobs] Run {RunId} could not be launched: {Message}", run.Id, message);
        await store.CompleteRunAsync(run.Id, JobRunStatus.Failed, null, message, CancellationToken.None);
    }

    private readonly record struct LaunchResultSnapshot(bool Success, string Message);
}
