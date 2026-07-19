using System.Text.Json;
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
            var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["commit"] = commit,
                ["repositoryPath"] = repositoryPath,
                ["trigger"] = "post-commit"
            };
            var contextJson = JsonSerializer.Serialize(
                metadata,
                AppJsonSerializerContext.Default.DictionaryStringString);
            await jobStore.EnqueueEventRunsAsync(
                repositoryPath,
                JobTriggerKind.Commit,
                commit.ToLowerInvariant(),
                contextJson,
                cancellationToken);
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
