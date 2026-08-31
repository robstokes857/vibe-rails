using Serilog;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Utils;
using VibeRails.Daemon;
using VibeRails.Daemon.Ipc;

namespace VibeRails.Services.Jobs;

public static class JobTriggerProcessHost
{
    public static bool IsRequested(IReadOnlyList<string> args) =>
        args.Any(argument => argument.Equals("--job-trigger", StringComparison.OrdinalIgnoreCase));

    public static Task<int> RunAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken = default) =>
        RunCoreAsync(
            args,
            jobStore: null,
            CreateDefaultKicker(),
            cancellationToken);

    internal static Task<int> RunAsync(
        IReadOnlyList<string> args,
        IJobStore jobStore,
        CancellationToken cancellationToken = default) =>
        RunCoreAsync(args, jobStore, daemonKicker: null, cancellationToken);

    internal static Task<int> RunAsync(
        IReadOnlyList<string> args,
        IJobStore jobStore,
        IJobDaemonKicker daemonKicker,
        CancellationToken cancellationToken = default) =>
        RunCoreAsync(args, jobStore, daemonKicker, cancellationToken);

    private static async Task<int> RunCoreAsync(
        IReadOnlyList<string> args,
        IJobStore? jobStore,
        IJobDaemonKicker? daemonKicker,
        CancellationToken cancellationToken)
    {
        try
        {
            var trigger = GetValue(args, "--job-trigger");
            // Before-commit automations are queued in-process by AutomatedWorkflowsPreflightStep
            // during `vb --vca-hook pre-commit`. This host is only the post-commit hook.
            if (!string.Equals(trigger, "post-commit", StringComparison.OrdinalIgnoreCase))
                return 0;

            var workingDirectory = GetValue(args, "--workdir") ?? Directory.GetCurrentDirectory();
            if (!Directory.Exists(workingDirectory))
                return 0;
            var rootResult = await GitProcessRunner.RunAsync(
                ["rev-parse", "--show-toplevel"],
                workingDirectory,
                TimeSpan.FromSeconds(5),
                cancellationToken);
            if (rootResult.ExitCode != 0 || rootResult.TimedOut || string.IsNullOrWhiteSpace(rootResult.StdOut))
                return 0;
            var repositoryPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootResult.StdOut.Trim()));

            var commit = GetValue(args, "--commit");
            if (string.IsNullOrWhiteSpace(commit))
            {
                var commitResult = await GitProcessRunner.RunAsync(
                    ["rev-parse", "HEAD"],
                    repositoryPath,
                    TimeSpan.FromSeconds(5),
                    cancellationToken);
                if (commitResult.ExitCode != 0 || commitResult.TimedOut)
                    return 0;
                commit = commitResult.StdOut.Trim();
            }
            if (string.IsNullOrWhiteSpace(commit))
                return 0;

            if (jobStore is null)
            {
                var installDirectory = PathConstants.GetInstallDirPath();
                Directory.CreateDirectory(installDirectory);
                var statePath = Path.Combine(installDirectory, PathConstants.STATE_FILENAME);
                jobStore = new JobStore($"Data Source={statePath};Mode=ReadWriteCreate;Cache=Shared");
            }

            // Enqueue only — deliberately. Spawning terminals from here used to be unbounded: this
            // loop called the launcher once per returned run with no cap, so a rebase or an amend
            // loop producing six commits in two minutes spawned six job processes at once. Handing
            // queued runs to the next active root scheduler means they inherit both the per-job
            // overlap guard and the machine-wide launch cap.
            //
            // Cost is a small amount of latency: an active root picks it up on its next scheduler
            // poll (normally within ten seconds) rather than launching directly from the Git hook.
            var runIds = await jobStore.EnqueueEventRunsAsync(
                repositoryPath,
                JobTriggerKind.Commit,
                commit.ToLowerInvariant(),
                cancellationToken);
            if (runIds.Count > 0)
            {
                Log.Information("[Jobs] Queued {Count} commit-triggered run(s) for {Repository}", runIds.Count, repositoryPath);
                await JobDaemonWakeup.TryKickAsync(daemonKicker, CancellationToken.None);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // post-commit automation is report-only and can never retroactively fail a commit.
            Log.Error(ex, "[Jobs] Failed to enqueue post-commit Jobs");
        }
        return 0;
    }

    private static string? GetValue(IReadOnlyList<string> args, string option)
    {
        for (var index = 0; index < args.Count; index++)
        {
            if (args[index].Equals(option, StringComparison.OrdinalIgnoreCase))
                return index + 1 < args.Count ? args[index + 1] : null;
            if (args[index].StartsWith(option + "=", StringComparison.OrdinalIgnoreCase))
                return args[index][(option.Length + 1)..];
        }
        return null;
    }

    private static IJobDaemonKicker? CreateDefaultKicker()
    {
        try
        {
            return new JobDaemonKicker(new DaemonControlClient(), new CurrentUserIdentityProvider());
        }
        catch (Exception ex)
        {
            // The Git hook must remain report-only even if current-user IPC identity is unavailable.
            Log.Debug(ex, "[VBD] Could not initialize the post-commit scheduler wakeup client");
            return null;
        }
    }
}
