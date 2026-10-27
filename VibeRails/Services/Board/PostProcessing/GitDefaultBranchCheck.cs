using System.Text.RegularExpressions;
using VibeRails.Services.Git;

namespace VibeRails.Services.Board.PostProcessing;

/// <summary>Whether any of a card's linked commits reached the project's default branch.</summary>
public enum BoardCommitLanding
{
    /// <summary>At least one linked commit, or an equivalent patch, is on the default branch.</summary>
    Landed,
    /// <summary>Git answered, and none of the linked commits is on the default branch.</summary>
    NotLanded,
    /// <summary>There is nothing to check against: no checkout, not a repository, or no default branch.</summary>
    Unverifiable,
    /// <summary>Git could not answer (timed out or failed to start); worth asking again later.</summary>
    Failed
}

public interface IBoardDefaultBranchCheck
{
    Task<BoardCommitLanding> CheckAsync(string projectPath, IReadOnlyList<string> shas, CancellationToken cancellationToken);
}

/// <summary>
/// Checks linked commits against the default branch as this machine's checkout knows it: the local
/// branch and its <c>origin</c> remote-tracking ref, never a fetch, so a background pass makes no
/// network call and can never raise a credential prompt. A commit counts when it is an ancestor of
/// either ref, or when <c>git cherry</c> finds an equivalent patch there (a rebase or the PR bot's
/// amend gives the landed commit a new SHA). A squash of several commits is not detected; a GitHub
/// PR verified as merged into the owning repository's default branch covers that case.
/// </summary>
public sealed partial class GitDefaultBranchCheck : IBoardDefaultBranchCheck
{
    private static readonly TimeSpan CherryTimeout = TimeSpan.FromSeconds(30);

    public async Task<BoardCommitLanding> CheckAsync(string projectPath, IReadOnlyList<string> shas, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(projectPath) || !Directory.Exists(projectPath))
            return BoardCommitLanding.Unverifiable;

        var repository = await GitCli.RunAsync(projectPath, ["rev-parse", "--git-dir"], cancellationToken);
        if (repository.TimedOut || repository.ExitCode < 0) return BoardCommitLanding.Failed;
        if (!repository.Succeeded) return BoardCommitLanding.Unverifiable;

        var refs = await DefaultBranchRefsAsync(projectPath, cancellationToken);
        if (refs is null) return BoardCommitLanding.Failed;
        if (refs.Count == 0) return BoardCommitLanding.Unverifiable;

        var failed = false;
        foreach (var sha in shas)
        {
            if (!ShaPattern().IsMatch(sha)) continue;
            var known = false;
            foreach (var reference in refs)
            {
                var ancestor = await GitCli.RunAsync(projectPath, ["merge-base", "--is-ancestor", sha, reference], cancellationToken);
                if (ancestor.TimedOut || ancestor.ExitCode < 0) { failed = true; continue; }
                if (ancestor.ExitCode == 0) return BoardCommitLanding.Landed;
                // 1 is "not an ancestor"; 128 is a commit this checkout does not have, which cannot
                // be on any branch it knows either.
                known |= ancestor.ExitCode == 1;
            }
            if (!known) continue;

            foreach (var reference in refs)
            {
                // "- <sha>" when the default branch already has an equivalent patch. A root commit
                // has no parent to limit the range, so it exits non-zero and simply does not count.
                var cherry = await GitCli.RunAsync(projectPath, ["cherry", reference, sha, sha + "^"], cancellationToken,
                    CherryTimeout, maxOutputChars: 4096);
                if (cherry.TimedOut || cherry.ExitCode < 0) { failed = true; continue; }
                if (cherry.Succeeded && cherry.StdOut.TrimStart().StartsWith('-')) return BoardCommitLanding.Landed;
            }
        }
        return failed ? BoardCommitLanding.Failed : BoardCommitLanding.NotLanded;
    }

    /// <summary>
    /// The local and <c>origin</c> refs of the default branch that exist: the branch
    /// <c>origin/HEAD</c> names, else <c>main</c>, else <c>master</c>. Null when git failed.
    /// </summary>
    internal static async Task<IReadOnlyList<string>?> DefaultBranchRefsAsync(string projectPath, CancellationToken cancellationToken)
    {
        var names = new List<string>();
        var head = await GitCli.RunAsync(projectPath, ["symbolic-ref", "-q", "refs/remotes/origin/HEAD"], cancellationToken);
        if (head.TimedOut || head.ExitCode < 0) return null;
        const string remotePrefix = "refs/remotes/origin/";
        var target = head.StdOut.Trim();
        if (head.Succeeded && target.StartsWith(remotePrefix, StringComparison.Ordinal) && target.Length > remotePrefix.Length)
            names.Add(target[remotePrefix.Length..]);
        names.Add("main");
        names.Add("master");

        foreach (var name in names.Distinct(StringComparer.Ordinal))
        {
            var refs = new List<string>();
            foreach (var reference in new[] { "refs/heads/" + name, remotePrefix + name })
            {
                var verify = await GitCli.RunAsync(projectPath, ["rev-parse", "-q", "--verify", reference + "^{commit}"], cancellationToken);
                if (verify.TimedOut || verify.ExitCode < 0) return null;
                if (verify.Succeeded) refs.Add(reference);
            }
            if (refs.Count > 0) return refs;
        }
        return [];
    }

    [GeneratedRegex("^[0-9a-fA-F]{7,64}$")]
    private static partial Regex ShaPattern();
}
