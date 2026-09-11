using System.Diagnostics;
using System.Text;

namespace VibeRails.Services.Git;

/// <summary>Outcome of one <c>git</c> invocation. <see cref="TimedOut"/> means the process was killed.</summary>
public sealed record GitCliResult(int ExitCode, string StdOut, string StdErr, bool TimedOut)
{
    public bool Succeeded => ExitCode == 0 && !TimedOut;
}

/// <summary>
/// Runs <c>git</c> with an argument list (never a shell string) and a hard timeout. Shared by the
/// VCA rule tool and the board's commit linking so every git call in the process has the same
/// no-shell, kill-on-timeout discipline.
/// </summary>
public static class GitCli
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan KillGracePeriod = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan DrainBound = TimeSpan.FromSeconds(2);

    /// <summary>Runs git, optionally retaining only a bounded prefix of each output stream.</summary>
    /// <param name="maxOutputChars">Maximum characters retained per stream; null keeps the full output.
    /// Excess output is drained through a fixed-size buffer so git can exit without blocking.</param>
    public static async Task<GitCliResult> RunAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null,
        int? maxOutputChars = null)
    {
        if (maxOutputChars is < 0)
            throw new ArgumentOutOfRangeException(nameof(maxOutputChars));

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);

        try
        {
            process.Start();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new GitCliResult(-1, string.Empty, ex.Message, TimedOut: false);
        }

        using var drainCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var stdoutTask = ReadOutputAsync(process.StandardOutput, maxOutputChars, drainCts.Token);
        var stderrTask = ReadOutputAsync(process.StandardError, maxOutputChars, drainCts.Token);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout ?? DefaultTimeout);

        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            timedOut = true;
            TryKill(process);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            drainCts.Cancel();
            Observe(stdoutTask);
            Observe(stderrTask);
            throw;
        }

        if (!process.HasExited)
        {
            await Task.WhenAny(
                process.WaitForExitAsync(CancellationToken.None),
                Task.Delay(KillGracePeriod, CancellationToken.None));
            if (!process.HasExited)
            {
                timedOut = true;
                drainCts.Cancel();
            }
        }

        var stdoutComplete = CompleteOutputAsync(stdoutTask);
        var stderrComplete = CompleteOutputAsync(stderrTask);
        var stdout = await stdoutComplete;
        var stderr = await stderrComplete;
        timedOut |= stdout.TimedOut || stderr.TimedOut;
        return new GitCliResult(process.HasExited ? process.ExitCode : -1, stdout.Text, stderr.Text, timedOut);
    }

    private static async Task<string> ReadOutputAsync(StreamReader reader, int? maxChars, CancellationToken cancellationToken)
    {
        if (maxChars is not { } limit)
            return await reader.ReadToEndAsync(cancellationToken);

        var buffer = new char[4096];
        var output = new StringBuilder(Math.Min(limit, buffer.Length));
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
        {
            var keep = Math.Min(read, limit - output.Length);
            if (keep > 0)
                output.Append(buffer, 0, keep);
        }
        return output.ToString();
    }

    private static async Task<(string Text, bool TimedOut)> CompleteOutputAsync(Task<string> outputTask)
    {
        try
        {
            return (await outputTask.WaitAsync(DrainBound), false);
        }
        catch (TimeoutException)
        {
            Observe(outputTask);
            return (string.Empty, true);
        }
        catch (OperationCanceledException)
        {
            Observe(outputTask);
            return (string.Empty, true);
        }
    }

    private static void Observe(Task task)
    {
        _ = task.ContinueWith(
            static t => _ = t.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>
    /// Walks up from <paramref name="startPath"/> to the directory containing <c>.git</c>. In a
    /// linked worktree or submodule <c>.git</c> is a file (a gitdir pointer), so both forms count.
    /// Pure filesystem — no process is spawned.
    /// </summary>
    public static string? FindRoot(string startPath)
    {
        var current = startPath;
        while (!string.IsNullOrEmpty(current))
        {
            var dotGit = Path.Combine(current, ".git");
            if (Directory.Exists(dotGit) || File.Exists(dotGit))
                return current;
            var parent = Directory.GetParent(current);
            if (parent == null) break;
            current = parent.FullName;
        }
        return null;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best effort: a git process that outlives the timeout is reported as timed out either way.
        }
    }
}
