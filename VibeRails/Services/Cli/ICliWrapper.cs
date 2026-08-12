namespace VibeRails.Services.Cli;

/// <summary>
/// A general-purpose, injectable, logged way to spin up a process.
///
/// Two shapes, deliberately: <see cref="RunAsync"/> for "run a thing and read its output" (hidden,
/// piped, captured), and <see cref="RunInNewTerminalAsync"/> for "run a thing the user watches"
/// (its own OS terminal window, no captured output, exit code recovered from a sentinel file).
///
/// Everything cross-cutting — Serilog <c>[Cli]</c> logging of command + duration + exit code,
/// timeout with kill-tree, cancellation normalisation, working-directory validation, env-var
/// merge — lives in the implementation so callers never re-solve it.
/// </summary>
public interface ICliWrapper
{
    /// <summary>
    /// Runs a process hidden with stdio piped. <paramref name="onLine"/> fires per line as it
    /// arrives (stdout and stderr interleaved, tagged via <see cref="CliOutputLine.IsError"/>);
    /// the full text is also buffered into the result either way.
    /// </summary>
    Task<CliResult> RunAsync(
        CliRequest request,
        Func<CliOutputLine, ValueTask>? onLine = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Spawns a visible OS terminal window and blocks until it finishes. No output is captured —
    /// the user watches it — so <see cref="CliResult.StandardOutput"/> and
    /// <see cref="CliResult.StandardError"/> are empty and the exit code arrives via the sentinel
    /// file the generated script writes.
    /// </summary>
    Task<CliResult> RunInNewTerminalAsync(
        CliTerminalRequest request,
        CancellationToken cancellationToken = default);
}
