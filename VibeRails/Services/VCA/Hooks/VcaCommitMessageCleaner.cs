using System.Text;
using VibeRails.Services.Git;

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
        // GetBytes emits no BOM; the runner sends these exact bytes, then closes stdin.
        var result = await GitCommandRunner.RunTextAsync(workingDirectory, ["stripspace", "--strip-comments"],
            cancellationToken, GitTimeout, standardInput: Encoding.UTF8.GetBytes(commitMessage));
        if (result.TimedOut)
        {
            throw new TimeoutException(
                $"git stripspace timed out after {(int)GitTimeout.TotalSeconds} seconds.");
        }
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git stripspace failed with exit code {result.ExitCode}: {result.StdErr.Trim()}");
        }
        return result.StdOut;
    }
}
