using Serilog;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services.Board;
using VibeRails.Services.LlmClis;
using VibeRails.Services.LlmClis.Launchers;
using VibeRails.Utils;

namespace VibeRails.Services.Jobs;

public interface IJobLaunchService
{
    /// <summary>
    /// The scheduler lease holder's pass: spawns terminals for every launchable queued run, except a
    /// Board run of another project that was queued less than
    /// <see cref="JobLaunchService.ForeignProjectBoardRunGrace"/> ago (its own window gets first
    /// claim). Returns how many were launched.
    /// </summary>
    Task<int> LaunchQueuedRunsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Any open root's pass, lease or not: spawns terminal tabs only for the queued Board runs whose
    /// project is this root's project, so the tab lands in the window the card lives in. Returns how
    /// many were launched.
    /// </summary>
    Task<int> LaunchQueuedProjectRunsAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Opens Board lane Automations in terminal tabs and all other triggers in native terminals. Script-only workflows launch the
/// VibeRails child directly; a workflow with a Worker uses the same Environment launch pipeline as
/// the Environment screen so its arguments and workspace policy remain exact.
///
/// Several roots can be open at once (one per VS Code window) and they share one scheduler lease.
/// A terminal tab exists only in the memory of the process that spawned it, so a Board run opened by
/// the lease holder for another project's card would be invisible in that project's window. Every
/// root therefore claims the queued Board runs of its own project each cycle, lease or not (the
/// <c>LaunchedUTC</c> row claim stops two roots from opening one run twice), and the lease holder
/// opens another project's Board run in its own window only after that run has waited
/// <see cref="ForeignProjectBoardRunGrace"/> with no window for the project claiming it.
///
/// There is no non-interactive Worker rewrite, auto-approval flag injection, or filtering of the
/// Environment's saved arguments. Repository-script validation and orchestration happen later in
/// the child process from the immutable run-action snapshot.
///
/// The spawned process (<c>vb --env … --job-run &lt;id&gt;</c>) owns the run from there: it claims it
/// under its own PID, records its own session, honours cancellation, enforces its own deadline, and
/// writes its own outcome. This service never waits on it.
/// </summary>
public sealed class JobLaunchService(
    IJobStore store,
    IEnvironmentLaunchService environmentLaunchService,
    IJobProcessLauncher processLauncher,
    IJobTerminalTabLauncher? terminalTabLauncher = null,
    IBoardProjectResolver? projectResolver = null) : IJobLaunchService
{
    /// <summary>
    /// Ceiling on simultaneously open job terminals across the whole machine. The per-job overlap
    /// guard in <see cref="JobStore"/> stops one job stacking windows; this stops many jobs coming
    /// due at once from burying the desktop. Each window is a full vb host plus a CLI process tree,
    /// so this is a real resource bound, not just a cosmetic one.
    /// </summary>
    public const int MaxConcurrentJobTerminals = 3;

    /// <summary>
    /// How long a queued Board run of another project waits for that project's own window before the
    /// lease holder opens it here instead. Three scheduler polls: long enough for an open root to
    /// claim its run, short enough that a run whose window is closed still starts promptly.
    /// </summary>
    internal static readonly TimeSpan ForeignProjectBoardRunGrace = TimeSpan.FromSeconds(30);

    public Task<int> LaunchQueuedRunsAsync(CancellationToken cancellationToken = default)
        => LaunchAsync(ownsSchedulerLease: true, cancellationToken);

    public Task<int> LaunchQueuedProjectRunsAsync(CancellationToken cancellationToken = default)
        => LaunchAsync(ownsSchedulerLease: false, cancellationToken);

    private async Task<int> LaunchAsync(bool ownsSchedulerLease, CancellationToken cancellationToken)
    {
        var launchable = await store.GetLaunchableRunsAsync(cancellationToken);
        if (launchable.Count == 0)
            return 0;

        var hostProjectPath = await ResolveHostProjectPathAsync(cancellationToken);
        var nowUtc = DateTime.UtcNow;
        var candidates = launchable
            .Where(run => LaunchesHere(run, ownsSchedulerLease, hostProjectPath, nowUtc))
            .ToList();
        if (candidates.Count == 0)
            return 0;

        // Terminals already open from earlier ticks count against the cap too — otherwise every
        // tick would happily open another full set on top of the ones still running.
        var alreadyOpen = await store.CountRunningRunsAsync(cancellationToken);
        var launched = 0;

        foreach (var run in candidates)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            if (alreadyOpen + launched >= MaxConcurrentJobTerminals)
            {
                Log.Information(
                    "[Jobs] Launch cap reached ({Cap}, {Open} already open); {Remaining} run(s) stay queued for the next tick",
                    MaxConcurrentJobTerminals, alreadyOpen, candidates.Count - launched);
                break;
            }

            // Claim before doing anything observable. The shared scheduler lease prevents normal
            // contention; this row claim is the handoff/crash safety net and the only barrier
            // between two open roots of the same project.
            if (!await store.TryMarkLaunchedAsync(run.Id, cancellationToken))
                continue;

            if (hostProjectPath is not null && IsBoardRunOfAnotherProject(run, hostProjectPath))
            {
                Log.Information(
                    "[Jobs] No open VibeRails window for {ProjectPath} claimed Board run {RunId} within {Grace}; opening it in this window ({HostProjectPath})",
                    run.ProjectPath, run.Id, ForeignProjectBoardRunGrace, hostProjectPath);
            }

            if (await LaunchOneAsync(run, cancellationToken))
                launched++;
        }

        return launched;
    }

    /// <summary>
    /// Whether this process opens <paramref name="run"/> now. Native-terminal runs belong to the
    /// lease holder. A Board run opens in the root whose project it belongs to, and falls back to
    /// the lease holder once <see cref="ForeignProjectBoardRunGrace"/> has passed unclaimed. A host
    /// that knows no project (no resolver) behaves as before: the lease holder opens everything.
    /// </summary>
    internal static bool LaunchesHere(JobRunRecord run, bool ownsSchedulerLease, string? hostProjectPath, DateTime nowUtc)
    {
        if (hostProjectPath is null || !JobBoardContext.OpensTerminalTab(run.TriggerKind, run.TriggerKey))
            return ownsSchedulerLease;
        if (!IsBoardRunOfAnotherProject(run, hostProjectPath))
            return true;
        // No open window for that project has claimed the run yet. The lease holder waits a
        // little, then opens it here rather than leaving it queued forever: the project's
        // window may simply be closed.
        return ownsSchedulerLease && nowUtc - run.QueuedUtc >= ForeignProjectBoardRunGrace;
    }

    private static bool IsBoardRunOfAnotherProject(JobRunRecord run, string hostProjectPath)
        => JobBoardContext.OpensTerminalTab(run.TriggerKind, run.TriggerKey)
            && !ProjectPathComparer.Matches(run.ProjectPath, hostProjectPath);

    private async Task<string?> ResolveHostProjectPathAsync(CancellationToken cancellationToken)
    {
        if (projectResolver is null)
            return null;
        try
        {
            var projectPath = await projectResolver.ResolveAsync(cancellationToken);
            return string.IsNullOrWhiteSpace(projectPath) ? null : projectPath;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warning(ex, "[Jobs] Could not resolve this root's project; Board runs launch from the lease holder as before");
            return null;
        }
    }

    private async Task<bool> LaunchOneAsync(JobRunRecord run, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(run.ProjectPath))
        {
            await FailAsync(run, $"The project directory no longer exists: {run.ProjectPath}");
            return false;
        }

        var actions = run.Actions?.OrderBy(action => action.Position).ToList() ?? [];
        var workers = actions.Where(action => action.Kind == JobActionKind.Worker).ToList();
        if (workers.Count > 1)
        {
            await FailAsync(run, "This Automation snapshot contains more than one Worker.");
            return false;
        }

        LaunchResultSnapshot result;
        try
        {
            LaunchResult launch;
            if (JobBoardContext.OpensTerminalTab(run.TriggerKind, run.TriggerKey))
            {
                launch = terminalTabLauncher is null
                    ? new LaunchResult(false, "Terminal tabs are unavailable in this host.")
                    : await terminalTabLauncher.LaunchAsync(run, cancellationToken);
            }
            else if (workers.Count == 1 || actions.Count == 0 && run.EnvironmentId is not null)
            {
                // Worker workflows retain the exact Environment launch pipeline, including its
                // persistent/per-run workspace resolution. Every repository script in the child
                // process then uses that resolved workspace root.
                var worker = workers.FirstOrDefault();
                var llm = worker?.Llm ?? run.Llm;
                var environmentId = worker?.EnvironmentId ?? run.EnvironmentId;
                var environmentName = worker?.EnvironmentName ?? run.EnvironmentName;
                launch = await environmentLaunchService.LaunchAsync(
                    llm,
                    new LaunchCliRequest(run.ProjectPath, environmentName, []),
                    run.ProjectPath,
                    BuildVbArgs(run),
                    keepTerminalOpen: false,
                    environmentId: environmentId,
                    launchMinimized: run.LaunchMinimized,
                    cancellationToken: cancellationToken);
            }
            else
            {
                // No Worker means there is no LLM launcher to borrow. Open the same native
                // terminal shape directly and let --job-run orchestrate the script actions.
                launch = processLauncher.Launch(
                    run.ProjectPath,
                    BuildStandaloneVbArgs(run),
                    run.LaunchMinimized);
            }
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
            "[Jobs] Launched run {RunId} for job '{JobName}' ({ActionCount} action(s), worker={Worker}) in {ProjectPath}; minimized={LaunchMinimized}",
            run.Id, run.JobName, actions.Count, workers.FirstOrDefault()?.EnvironmentName ?? run.EnvironmentName ?? "none",
            run.ProjectPath, run.LaunchMinimized);
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

    /// <summary>Complete argv for a script-only child, which has no --env launcher to add workdir.</summary>
    public static string[] BuildStandaloneVbArgs(JobRunRecord run) =>
        ["--workdir", run.ProjectPath, .. BuildVbArgs(run)];

    private async Task FailAsync(JobRunRecord run, string message)
    {
        Log.Warning("[Jobs] Run {RunId} could not be launched: {Message}", run.Id, message);
        await store.CompleteRunAsync(run.Id, JobRunStatus.Failed, null, message, CancellationToken.None);
    }

    private readonly record struct LaunchResultSnapshot(bool Success, string Message);
}
