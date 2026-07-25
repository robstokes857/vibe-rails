using Serilog;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Utils;

namespace VibeRails.Services.Jobs;

public static class JobTriggerProcessHost
{
    public static bool IsRequested(IReadOnlyList<string> args) =>
        args.Any(argument => argument.Equals("--job-trigger", StringComparison.OrdinalIgnoreCase));

    public static Task<int> RunAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken = default) =>
        RunCoreAsync(args, jobStore: null, cancellationToken);

    internal static Task<int> RunAsync(
        IReadOnlyList<string> args,
        IJobStore jobStore,
        CancellationToken cancellationToken = default) =>
        RunCoreAsync(args, jobStore, cancellationToken);

    private static async Task<int> RunCoreAsync(
        IReadOnlyList<string> args,
        IJobStore? jobStore,
        CancellationToken cancellationToken)
    {
        try
        {
            var trigger = GetValue(args, "--job-trigger");
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
            // the queued runs to the tick (or the dashboard scheduler, whichever comes first) means
            // they inherit both the per-job overlap guard and the machine-wide launch cap.
            //
            // Cost is latency: a commit-triggered run starts within a minute rather than instantly.
            // For an automated review that is not a meaningful difference.
            var runIds = await jobStore.EnqueueEventRunsAsync(
                repositoryPath,
                JobTriggerKind.Commit,
                commit.ToLowerInvariant(),
                cancellationToken);
            if (runIds.Count > 0)
            {
                Log.Information("[Jobs] Queued {Count} commit-triggered run(s) for {Repository}", runIds.Count, repositoryPath);
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
}
