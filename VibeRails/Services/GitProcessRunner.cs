using System.Diagnostics;
using Serilog;
using VibeRails.Services.Git;

namespace VibeRails.Services;

/// <summary>
/// Text-oriented git runner for services that want trimmed output and a timeout flag instead of an
/// exception. Every child is launched through <see cref="GitCli.CreateStartInfo"/>: argv only, stdin
/// redirected and closed, fsmonitor off. Arguments are never shell text.
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
        var displayArguments = string.Join(' ', arguments);
        using var process = new Process { StartInfo = GitCli.CreateStartInfo(workingDirectory, arguments) };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            Log.Warning(
                ex,
                "[Git] Failed to start git {Arguments} in {WorkingDirectory}",
                displayArguments,
                workingDirectory);
            return new Result(-1, string.Empty, ex.Message, false);
        }

        // EOF on stdin at once. A git child that inherits this process's stdin pipe hangs at startup
        // while the parent has a read pending on it, which the stdio MCP host always does during a
        // tool call. See GitCli.CreateStartInfo.
        try { process.StandardInput.Close(); }
        catch { /* the write end still closes when the process is disposed, so git still gets EOF */ }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        var timedOut = false;

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            timedOut = true;
            TryKill(process);
            await WaitForExitSilentlyAsync(process, TimeSpan.FromSeconds(2));
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        var stdout = await ReadSilentlyAsync(stdoutTask, TimeSpan.FromSeconds(1));
        var stderr = await ReadSilentlyAsync(stderrTask, TimeSpan.FromSeconds(1));

        return new Result(
            process.HasExited ? process.ExitCode : -1,
            trimOutput ? stdout.Trim() : stdout,
            stderr.Trim(),
            timedOut);
    }

    private static async Task<string> ReadSilentlyAsync(Task<string> readTask, TimeSpan timeout)
    {
        try
        {
            var completedTask = await Task.WhenAny(readTask, Task.Delay(timeout));
            if (completedTask != readTask)
            {
                return string.Empty;
            }

            return await readTask;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void TryKill(Process process)
    {
        if (process.HasExited)
        {
            return;
        }

        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best-effort only.
        }
    }

    private static async Task WaitForExitSilentlyAsync(Process process, TimeSpan timeout)
    {
        if (process.HasExited)
        {
            return;
        }

        try
        {
            await Task.WhenAny(process.WaitForExitAsync(), Task.Delay(timeout));
        }
        catch
        {
            // Best-effort only.
        }
    }
}
