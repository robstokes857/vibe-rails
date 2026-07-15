using System.Diagnostics;

namespace VibeRails.Services.VCA.Hooks;

/// <summary>
/// Normalizes a proposed commit message the same way Git removes comment lines.
/// Running <c>git stripspace --strip-comments</c> honors repository settings such
/// as <c>core.commentChar</c> (including <c>auto</c>), so a token Git will discard
/// can never satisfy VCA's acknowledgment audit.
/// </summary>
internal static class VcaCommitMessageCleaner
{
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(5);

    public static async Task<string> StripCommentsAsync(
        string commitMessage,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workingDirectory,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add("stripspace");
        process.StartInfo.ArgumentList.Add("--strip-comments");

        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.StandardInput.WriteAsync(commitMessage.AsMemory(), cancellationToken);
        await process.StandardInput.FlushAsync(cancellationToken);
        process.StandardInput.Close();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(GitTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new TimeoutException(
                $"git stripspace timed out after {(int)GitTimeout.TotalSeconds} seconds.");
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git stripspace failed with exit code {process.ExitCode}: {error.Trim()}");
        }

        return output;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Preserve the timeout or cancellation that triggered cleanup.
        }
    }
}
