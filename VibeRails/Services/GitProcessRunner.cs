using Serilog;
using VibeRails.Services.Git;

namespace VibeRails.Services;

/// <summary>
/// Text-oriented git runner for services that want trimmed output and a timeout flag instead of an
/// exception. Every child is launched through <see cref="GitCommandRunner"/>: argv only, stdin
/// redirected and closed, fsmonitor off. Arguments are never shell text.
/// Process failures are returned; caller cancellation still throws.
/// </summary>
internal static class GitProcessRunner
{
    internal sealed record Result(int ExitCode, string StdOut, string StdErr, bool TimedOut);

    public static Task<Result> RunAsync(
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
        => RunAsync(arguments, workingDirectory, timeout, trimOutput: true, cancellationToken);

    /// <summary>Same as <see cref="RunAsync(IReadOnlyList{string}, string, TimeSpan, CancellationToken)"/> but keeps stdout untrimmed (NUL-separated listings).</summary>
    public static Task<Result> RunRawAsync(
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
        => RunAsync(arguments, workingDirectory, timeout, trimOutput: false, cancellationToken);

    private static async Task<Result> RunAsync(
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        bool trimOutput,
        CancellationToken cancellationToken)
    {
        var result = await GitCommandRunner.RunTextAsync(workingDirectory, arguments, cancellationToken, timeout);
        if (result.ExitCode == -1 && !result.TimedOut)
            Log.Warning("[Git] Failed to run git {Arguments} in {WorkingDirectory}: {Reason}",
                string.Join(' ', arguments), workingDirectory, result.StdErr);
        return new Result(result.ExitCode,
            trimOutput ? result.StdOut.Trim() : result.StdOut, result.StdErr.Trim(), result.TimedOut);
    }
}
