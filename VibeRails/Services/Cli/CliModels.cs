namespace VibeRails.Services.Cli;

/// <summary>
/// One hidden, stdio-piped process run. The caller supplies a resolved executable and real argv
/// elements — nothing here is ever pasted into a command string, so arguments containing spaces,
/// quotes, or shell metacharacters survive intact.
/// </summary>
public sealed record CliRequest(
    string Executable,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string?>? EnvironmentVariables = null,
    TimeSpan? Timeout = null,
    string? StandardInput = null);

/// <summary>
/// One visible OS terminal window running <paramref name="ScriptBody"/>. The wrapper writes the
/// temp script, launches the platform's terminal, and blocks until the script finishes.
/// </summary>
/// <param name="ScriptBody">
/// Shell script text in the host platform's language (PowerShell on Windows, POSIX sh elsewhere).
/// This is the user's own command text; the wrapper wraps it with the directory guard, exit-code
/// capture, and sentinel write.
/// </param>
/// <param name="StartMinimized">
/// Start the window minimized so it does not steal focus. Honoured on Windows; the macOS and Linux
/// terminal launchers have no equivalent and ignore it.
/// </param>
public sealed record CliTerminalRequest(
    string ScriptBody,
    string WorkingDirectory,
    bool StartMinimized = false,
    TimeSpan? Timeout = null,
    IReadOnlyDictionary<string, string?>? EnvironmentVariables = null);

/// <summary>One line of interleaved child output, stamped with how long into the run it arrived.</summary>
public readonly record struct CliOutputLine(bool IsError, string Text, TimeSpan Elapsed);

/// <summary>
/// The outcome of one run. <see cref="ExitCode"/> is authoritative only when
/// <see cref="IsSuccess"/> is consulted alongside <see cref="TimedOut"/> and
/// <see cref="Cancelled"/> — a killed process reports whatever code the OS assigned it.
/// </summary>
public sealed record CliResult(
    int ExitCode,
    bool TimedOut,
    bool Cancelled,
    string StandardOutput,
    string StandardError,
    TimeSpan Duration,
    string CommandLine)
{
    public bool IsSuccess => ExitCode == 0 && !TimedOut && !Cancelled;

    /// <summary>A short, log-and-toast friendly description of how the run ended.</summary>
    public string DescribeFailure() =>
        TimedOut ? "timed out"
        : Cancelled ? "was cancelled"
        : ExitCode == 0 ? "succeeded"
        : $"exited with code {ExitCode}";
}
