using System.Diagnostics;
using System.Text;
using VibeRails.Utils;

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
    public static readonly TimeSpan DrainBound = TimeSpan.FromSeconds(2);

    /// <summary>Runs git, optionally retaining only a bounded prefix of each output stream.</summary>
    /// <param name="maxOutputChars">Maximum characters retained per stream; null keeps the full output.
    /// Excess output is drained through a fixed-size buffer so git can exit without blocking.</param>
    /// <param name="environment">Variables set for this child only (for example <c>GIT_INDEX_FILE</c>
    /// pointing a plumbing command at a temporary index). Nothing else about the launch changes.</param>
    /// <param name="standardInput">Bytes written to git's stdin before it is closed, for commands that
    /// read their input (<c>update-index --index-info</c>, <c>hash-object --stdin</c>). Stdin is closed
    /// either way, so git always gets EOF.</param>
    public static async Task<GitCliResult> RunAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null,
        int? maxOutputChars = null,
        IReadOnlyDictionary<string, string>? environment = null,
        ReadOnlyMemory<byte>? standardInput = null)
    {
        var result = await GitCommandRunner.RunTextAsync(workingDirectory, arguments, cancellationToken,
            timeout, maxOutputChars, environment, standardInput);
        return new GitCliResult(result.ExitCode, result.StdOut, result.StdErr, result.TimedOut);
    }

    /// <summary>
    /// Shared launch settings for <see cref="GitCommandRunner"/> and the sole process-lifecycle
    /// exception, the interactive GitBlobBatchReader. All one-shot callers use the runner, which
    /// closes stdin through CliWrap after any explicit bytes and bounds output draining after exit.
    /// The batch reader keeps stdin open for requests until disposal. <c>GitCliTests</c> enforces
    /// these two consumers; do not start another process directly from these settings.
    /// </summary>
    /// <remarks>
    /// The runner applies these settings through CliWrap's process-configuration hook to retain
    /// the actual ArgumentList. CliWrap owns pipe IO and forceful cancellation, while the runner
    /// arms DrainBound at process exit because a descendant can retain the output handles.
    /// Two guards, both required.
    /// <para>
    /// Stdin is always redirected. A git child that inherits this process's stdin pipe blocks at
    /// startup for as long as the parent has a read pending on that pipe, and the stdio MCP host's
    /// JSON-RPC reader always has one while a tool call runs. Reproduced 2026-10-11: rev-parse,
    /// merge-base and ls-files all ran until killed, fsmonitor on or off, while the same command
    /// with a closed redirected stdin finished in about 25 ms. A shell never shows it because a
    /// shell has no pending read, so "works from the terminal, times out in the MCP host" is this.
    /// </para>
    /// <para>
    /// <c>core.fsmonitor=false</c> keeps index-reading commands from hand-shaking with or spawning
    /// the fsmonitor daemon, which can inherit the redirected pipes and hold them past git's exit,
    /// so the output drain never sees EOF (VB-14).
    /// </para>
    /// <para>
    /// The directory reaches git as <c>-C &lt;dir&gt;</c>, not <see cref="ProcessStartInfo.WorkingDirectory"/>.
    /// Git changes into it before reading config, the index or any relative path, so this runs git
    /// exactly as if it had started there, and <c>-C</c> always consumes the next argv entry as a
    /// path, so a directory that begins with <c>-</c> cannot become an option. CodeQL's
    /// cs/command-line-injection treats <c>WorkingDirectory</c> as a command sink, and every route
    /// that names a project path reaches this method, so setting it raised alert #52. A missing
    /// directory now fails inside git (exit 128, "cannot change to") instead of at process start.
    /// </para>
    /// </remarks>
    internal static ProcessStartInfo CreateStartInfo(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var startInfo = new ProcessStartInfo
        {
            // Absolute, from PATH: a bare name would be looked up in the current directory (the
            // project) before PATH. See PathExecutable.
            FileName = PathExecutable.Resolve("git"),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // Git's paths and text output use UTF-8, independently of the Windows console code page.
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        // Empty keeps the host's current directory, as an empty WorkingDirectory did.
        if (!string.IsNullOrEmpty(workingDirectory))
        {
            startInfo.ArgumentList.Add("-C");
            startInfo.ArgumentList.Add(workingDirectory);
        }
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("core.fsmonitor=false");
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        if (environment is not null)
        {
            foreach (var (name, value) in environment)
                startInfo.Environment[name] = value;
        }
        return startInfo;
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
}
