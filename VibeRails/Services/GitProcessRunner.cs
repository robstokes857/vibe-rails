using System.Diagnostics;
using Serilog;

namespace VibeRails.Services;

internal static class GitProcessRunner
{
    internal sealed record Result(int ExitCode, string StdOut, string StdErr, bool TimedOut);

    public static async Task<Result> RunAsync(
        string arguments,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workingDirectory
            }
        };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Git] Failed to start git {Arguments} in {WorkingDirectory}", arguments, workingDirectory);
            return new Result(-1, string.Empty, ex.Message, false);
        }

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
            stdout.Trim(),
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
