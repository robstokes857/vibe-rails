using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using CliWrap;
using Serilog;
using VibeRails.Utils;

namespace VibeRails.Services.Cli;

/// <summary>
/// The default <see cref="ICliWrapper"/>. Holds no per-run state, so one singleton serves every
/// caller and every call is safe to make concurrently.
/// </summary>
public sealed class CliWrapper : ICliWrapper
{
    /// <summary>Used when a request leaves <c>Timeout</c> null.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan SentinelPollInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// How long to keep looking for the sentinel after the terminal process is seen to exit. The
    /// script writes the sentinel and then exits, so on a fast machine the two are observed in the
    /// wrong order often enough to matter.
    /// </summary>
    private static readonly TimeSpan SentinelGraceAfterExit = TimeSpan.FromSeconds(2);

    public async Task<CliResult> RunAsync(
        CliRequest request,
        Func<CliOutputLine, ValueTask>? onLine = null,
        CancellationToken cancellationToken = default)
    {
        var commandLine = DescribeCommand(request.Executable, request.Arguments);
        var stopwatch = Stopwatch.StartNew();

        if (!TryResolveWorkingDirectory(request.WorkingDirectory, out var workingDirectory, out var directoryError))
            return WorkingDirectoryFailure(directoryError, stopwatch.Elapsed, commandLine);

        var stdOut = new StringBuilder();
        var stdErr = new StringBuilder();

        // stdout and stderr are piped concurrently, so a callback that writes to a single stream
        // (the SSE response, say) has to be serialized here rather than in every caller.
        using var emitGate = new SemaphoreSlim(1, 1);

        async Task EmitAsync(bool isError, string text, CancellationToken token)
        {
            if (onLine is null) return;

            await emitGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                await onLine(new CliOutputLine(isError, text, stopwatch.Elapsed)).ConfigureAwait(false);
            }
            finally
            {
                emitGate.Release();
            }
        }

        // Buffer everything (the result carries the full text) AND fan each line out live.
        // PipeTarget.Merge over ToDelegate + ToStringBuilder is the same technique PyBridge's
        // PythonRunner uses.
        var stdOutPipe = onLine is null
            ? PipeTarget.ToStringBuilder(stdOut)
            : PipeTarget.Merge(
                PipeTarget.ToStringBuilder(stdOut),
                PipeTarget.ToDelegate((line, token) => EmitAsync(false, line, token)));

        var stdErrPipe = onLine is null
            ? PipeTarget.ToStringBuilder(stdErr)
            : PipeTarget.Merge(
                PipeTarget.ToStringBuilder(stdErr),
                PipeTarget.ToDelegate((line, token) => EmitAsync(true, line, token)));

        // Fully qualified: this namespace's own last segment is "Cli", which otherwise wins over
        // CliWrap's static Cli class during name resolution.
        var command = CliWrap.Cli.Wrap(request.Executable)
            .WithArguments(request.Arguments)
            .WithWorkingDirectory(workingDirectory)
            .WithValidation(CommandResultValidation.None)   // exit codes are surfaced, not thrown
            .WithStandardOutputPipe(stdOutPipe)
            .WithStandardErrorPipe(stdErrPipe);

        if (request.EnvironmentVariables is { Count: > 0 })
            command = command.WithEnvironmentVariables(request.EnvironmentVariables);

        if (request.StandardInput is not null)
            command = command.WithStandardInputPipe(PipeSource.FromString(request.StandardInput, Encoding.UTF8));

        var timeout = request.Timeout ?? DefaultTimeout;
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        var exitCode = -1;
        var timedOut = false;
        var cancelled = false;

        try
        {
            var result = await command.ExecuteAsync(linkedCts.Token).ConfigureAwait(false);
            exitCode = result.ExitCode;
        }
        catch (OperationCanceledException)
        {
            // CliWrap has already killed the process tree by the time this surfaces. Which token
            // fired decides how the caller should describe it.
            if (cancellationToken.IsCancellationRequested)
                cancelled = true;
            else
                timedOut = true;
        }
        catch (Win32Exception ex)
        {
            stopwatch.Stop();
            Log.Warning(ex, "[Cli] Could not start {CommandLine}", commandLine);
            return new CliResult(
                TerminalScriptBuilder.LaunchFailedExitCode,
                TimedOut: false,
                Cancelled: false,
                stdOut.ToString(),
                Append(stdErr.ToString(), $"Could not start '{request.Executable}': {ex.Message}"),
                stopwatch.Elapsed,
                commandLine);
        }
        finally
        {
            stopwatch.Stop();
        }

        var cliResult = new CliResult(
            exitCode,
            timedOut,
            cancelled,
            stdOut.ToString(),
            stdErr.ToString(),
            stopwatch.Elapsed,
            commandLine);

        LogOutcome(cliResult);
        return cliResult;
    }

    public async Task<CliResult> RunInNewTerminalAsync(
        CliTerminalRequest request,
        CancellationToken cancellationToken = default)
    {
        var commandLine = DescribeScript(request.ScriptBody);
        var stopwatch = Stopwatch.StartNew();

        if (!TryResolveWorkingDirectory(request.WorkingDirectory, out var workingDirectory, out var directoryError))
            return WorkingDirectoryFailure(directoryError, stopwatch.Elapsed, commandLine);

        var isWindows = OperatingSystem.IsWindows();
        var sentinelPath = Path.Combine(Path.GetTempPath(), $"viberails-step-{Guid.NewGuid():N}.exit");

        using var script = TerminalScriptBuilder.WriteScript(
            scriptPath => TerminalScriptBuilder.BuildScript(
                request.ScriptBody,
                workingDirectory,
                request.EnvironmentVariables,
                sentinelPath,
                pauseOnFailure: true,
                selfDeletePath: scriptPath,
                isWindows),
            isWindows);

        Process? process;
        try
        {
            process = StartTerminal(script, workingDirectory, request.StartMinimized, isWindows);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            Log.Error(ex, "[Cli] Failed to open a terminal window for {CommandLine}", commandLine);
            TryDelete(sentinelPath);
            return new CliResult(
                TerminalScriptBuilder.LaunchFailedExitCode,
                TimedOut: false,
                Cancelled: false,
                StandardOutput: string.Empty,
                StandardError: $"Could not open a terminal window: {ex.Message}",
                stopwatch.Elapsed,
                commandLine);
        }

        try
        {
            var result = await AwaitTerminalCompletionAsync(
                process,
                sentinelPath,
                request.Timeout ?? DefaultTimeout,
                stopwatch,
                commandLine,
                cancellationToken).ConfigureAwait(false);

            LogOutcome(result);
            return result;
        }
        finally
        {
            process?.Dispose();
            TryDelete(sentinelPath);
        }
    }

    /// <summary>
    /// Completion is a race between three things: the sentinel file appearing, the process exiting,
    /// and the timeout. That one rule covers every platform — on Windows the process handle is real
    /// and can also be killed; on macOS/Linux the terminal launcher detaches, so the sentinel is the
    /// only signal and a timeout is enforced by giving up on the poll.
    /// </summary>
    private static async Task<CliResult> AwaitTerminalCompletionAsync(
        Process? process,
        string sentinelPath,
        TimeSpan timeout,
        Stopwatch stopwatch,
        string commandLine,
        CancellationToken cancellationToken)
    {
        TimeSpan? exitObservedAt = null;

        while (true)
        {
            if (TryReadSentinel(sentinelPath, out var sentinelExitCode))
            {
                stopwatch.Stop();
                return new CliResult(
                    sentinelExitCode, TimedOut: false, Cancelled: false,
                    string.Empty, string.Empty, stopwatch.Elapsed, commandLine);
            }

            if (process is { HasExited: true })
            {
                // The script writes the sentinel and then exits; observing those out of order is
                // common enough that the exit alone is not proof the sentinel is never coming.
                exitObservedAt ??= stopwatch.Elapsed;
                if (stopwatch.Elapsed - exitObservedAt.Value >= SentinelGraceAfterExit)
                {
                    stopwatch.Stop();
                    var exitCode = SafeExitCode(process);
                    Log.Warning(
                        "[Cli] Terminal window for {CommandLine} exited without writing its exit-code sentinel; using process exit {ExitCode}",
                        commandLine,
                        exitCode);
                    return new CliResult(
                        exitCode, TimedOut: false, Cancelled: false,
                        string.Empty, string.Empty, stopwatch.Elapsed, commandLine);
                }
            }

            if (cancellationToken.IsCancellationRequested)
            {
                KillTree(process, commandLine);
                stopwatch.Stop();
                return new CliResult(
                    TerminalScriptBuilder.LaunchFailedExitCode, TimedOut: false, Cancelled: true,
                    string.Empty, string.Empty, stopwatch.Elapsed, commandLine);
            }

            if (stopwatch.Elapsed >= timeout)
            {
                // A window the user closed before the sentinel was written lands here too, and is
                // deliberately treated as a failure rather than an unknown success.
                KillTree(process, commandLine);
                stopwatch.Stop();
                return new CliResult(
                    TerminalScriptBuilder.LaunchFailedExitCode, TimedOut: true, Cancelled: false,
                    string.Empty, string.Empty, stopwatch.Elapsed, commandLine);
            }

            // CancellationToken.None: cancellation is handled by the branch above so the loop always
            // exits through one place, with the kill already done.
            await Task.Delay(SentinelPollInterval, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static Process? StartTerminal(
        CliScriptFile script,
        string workingDirectory,
        bool startMinimized,
        bool isWindows)
    {
        if (isWindows)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = script.Executable,
                WorkingDirectory = workingDirectory,
                // Required for a real window. It also means env vars cannot be set through
                // ProcessStartInfo, which is why they are baked into the script instead.
                UseShellExecute = true,
                WindowStyle = startMinimized ? ProcessWindowStyle.Minimized : ProcessWindowStyle.Normal,
            };
            foreach (var argument in script.Arguments)
                startInfo.ArgumentList.Add(argument);

            return Process.Start(startInfo)
                ?? throw new InvalidOperationException(
                    "pwsh (PowerShell Core) is required but was not found. Install it from https://github.com/PowerShell/PowerShell");
        }

        var sourceCommand = TerminalScriptBuilder.BuildPosixSourceCommand(script.Path);

        if (OperatingSystem.IsMacOS())
        {
            // Terminal.app is driven through osascript, which returns immediately — the handle it
            // gives back belongs to osascript, not to the step. Discarded on purpose.
            var startInfo = MacTerminalCommandBuilder.BuildStartInfo(
                MacTerminalCommandBuilder.BuildZshLaunchCommand(sourceCommand, keepShellOpen: false));
            using var launcher = Process.Start(startInfo);
            return null;
        }

        var shell = ShellDefaults.LinuxShell;
        (string Terminal, string[] Args)[] terminals =
        [
            ("gnome-terminal", ["--", shell, "-lic", sourceCommand]),
            ("konsole",        ["-e", shell, "-lic", sourceCommand]),
            ("xfce4-terminal", ["-e", shell, "-lic", sourceCommand]),
            ("xterm",          ["-e", shell, "-lic", sourceCommand]),
        ];

        foreach (var (terminal, args) in terminals)
        {
            try
            {
                var startInfo = new ProcessStartInfo { FileName = terminal, UseShellExecute = false };
                foreach (var argument in args)
                    startInfo.ArgumentList.Add(argument);

                using var launcher = Process.Start(startInfo);
                // Several of these fork and exit immediately, so the handle proves nothing about
                // the step. The sentinel is the signal on this platform.
                return null;
            }
            catch
            {
                // Terminal emulator not installed; try the next one.
            }
        }

        throw new InvalidOperationException(
            "No supported terminal emulator found (tried gnome-terminal, konsole, xfce4-terminal, xterm).");
    }

    private static bool TryReadSentinel(string sentinelPath, out int exitCode)
    {
        exitCode = 0;
        try
        {
            if (!File.Exists(sentinelPath))
                return false;

            var text = File.ReadAllText(sentinelPath).Trim();
            // An empty or half-written file means the script is mid-write; keep polling.
            return int.TryParse(text, out exitCode);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static int SafeExitCode(Process process)
    {
        try { return process.ExitCode; }
        catch { return TerminalScriptBuilder.LaunchFailedExitCode; }
    }

    private static void KillTree(Process? process, string commandLine)
    {
        if (process is null || process.HasExited)
            return;

        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Cli] Could not kill the terminal process tree for {CommandLine}", commandLine);
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* best effort */ }
    }

    private static bool TryResolveWorkingDirectory(string? requested, out string resolved, out string error)
    {
        resolved = string.Empty;
        error = string.Empty;

        try
        {
            var path = string.IsNullOrWhiteSpace(requested)
                ? Directory.GetCurrentDirectory()
                : Environment.ExpandEnvironmentVariables(requested.Trim());

            var fullPath = Path.GetFullPath(path);
            if (!Directory.Exists(fullPath))
            {
                error = $"Working directory does not exist: {fullPath}";
                return false;
            }

            resolved = fullPath;
            return true;
        }
        catch (Exception ex)
        {
            error = $"Invalid working directory '{requested}': {ex.Message}";
            return false;
        }
    }

    private static CliResult WorkingDirectoryFailure(string error, TimeSpan elapsed, string commandLine)
    {
        Log.Warning("[Cli] {Error} (command: {CommandLine})", error, commandLine);
        return new CliResult(
            TerminalScriptBuilder.WorkingDirectoryUnavailableExitCode,
            TimedOut: false,
            Cancelled: false,
            StandardOutput: string.Empty,
            StandardError: error,
            elapsed,
            commandLine);
    }

    private static void LogOutcome(CliResult result)
    {
        if (result.IsSuccess)
        {
            Log.Information(
                "[Cli] {CommandLine} succeeded in {DurationMs}ms",
                result.CommandLine,
                (long)result.Duration.TotalMilliseconds);
            return;
        }

        Log.Warning(
            "[Cli] {CommandLine} {Outcome} after {DurationMs}ms",
            result.CommandLine,
            result.DescribeFailure(),
            (long)result.Duration.TotalMilliseconds);
    }

    private static string Append(string existing, string line) =>
        string.IsNullOrEmpty(existing) ? line : existing + Environment.NewLine + line;

    internal static string DescribeCommand(string executable, IReadOnlyList<string> arguments)
    {
        var builder = new StringBuilder(executable);
        foreach (var argument in arguments)
        {
            builder.Append(' ');
            builder.Append(argument.Length == 0 || argument.Contains(' ') ? $"\"{argument}\"" : argument);
        }

        return Truncate(builder.ToString());
    }

    /// <summary>
    /// A step's script body is multi-line user text. Logs and toasts want one readable line, so
    /// take the first non-blank one.
    /// </summary>
    internal static string DescribeScript(string scriptBody)
    {
        foreach (var line in scriptBody.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0)
                return Truncate(trimmed);
        }

        return "(empty command)";
    }

    private static string Truncate(string value) =>
        value.Length <= 200 ? value : value[..200] + "…";
}
