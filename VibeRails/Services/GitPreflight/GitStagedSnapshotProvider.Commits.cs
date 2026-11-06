using System.Globalization;
using VibeRails.DTOs;

namespace VibeRails.Services.GitPreflight;

public sealed partial class GitStagedSnapshotProvider
{
    private const string EmptyTree = "4b825dc642cb6eb9a060e54bf8d69288fbee4904";

    /// <summary>
    /// Captures the selected commits' surviving additions at their newest tree. Interleaved
    /// commits are excluded from scoring, including additions in the same files. Requires one
    /// first-parent lineage so a merge cannot silently widen the selected work.
    /// </summary>
    public async Task<GitStagedSnapshot> CaptureCommitsAsync(string repositoryPath, IReadOnlyList<string> commits,
        bool includeImpactFiles, CancellationToken cancellationToken)
    {
        if (commits.Count == 0) throw new ArgumentException("Select at least one commit.");
        var resolved = new HashSet<string>(StringComparer.Ordinal);
        foreach (var commit in commits)
        {
            if (!JobCheckScope.IsCommitId(commit)) throw new ArgumentException("Invalid commit SHA.");
            var revision = await RunTextGitAsync(repositoryPath, ["rev-parse", "--verify", commit + "^{commit}"], cancellationToken);
            EnsureSucceeded(revision, "resolve selected commit", repositoryPath);
            resolved.Add(revision.StdOut.Trim());
        }
        var order = await RunTextGitAsync(repositoryPath, ["rev-list", "--topo-order", "--max-count=1", .. resolved, "--"], cancellationToken);
        EnsureSucceeded(order, "order selected commits", repositoryPath);
        var ordered = order.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var head = ordered[0];
        // The first-parent chain matches each commit's diff, including a merge's diff to its
        // first parent. Side-branch selections would need a separate report, not a wider range.
        var lineage = await RunTextGitAsync(repositoryPath, ["rev-list", "--first-parent", head], cancellationToken);
        EnsureSucceeded(lineage, "resolve selected commit lineage", repositoryPath);
        var chain = lineage.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (resolved.Any(sha => !chain.Contains(sha, StringComparer.Ordinal)))
            throw new InvalidOperationException("Linked commits must share a first-parent history. Run separate commit checks for divergent commits.");
        var oldest = chain.Last(resolved.Contains);
        var parent = await RunTextGitAsync(repositoryPath, ["rev-list", "--parents", "-n", "1", oldest], cancellationToken);
        EnsureSucceeded(parent, "resolve selected commit parent", repositoryPath);
        var parents = parent.StdOut.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var baseline = parents.Length > 1 ? parents[1] : EmptyTree;
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sha in resolved)
        {
            var changed = await RunTextGitAsync(repositoryPath,
                ["diff-tree", "--no-ext-diff", "--no-textconv", "--no-commit-id", "--name-status", "-z", "-r", "-M", "--root", "--diff-merges=first-parent", sha, "--"], cancellationToken);
            EnsureSucceeded(changed, "read selected commit files", repositoryPath);
            foreach (var entry in ParseNameStatus(changed.StdOut))
            {
                paths.Add(entry.RelativePath);
                if (entry.PreviousRelativePath is { } previous) paths.Add(previous);
            }
        }
        var snapshot = await CaptureCommittedCoreAsync(repositoryPath, baseline, head, includeImpactFiles, cancellationToken, paths);
        if (resolved.Count > 1)
        {
            var files = new List<GitStagedFileSnapshot>();
            foreach (var file in snapshot.Files)
            {
                if (file.Content is null || file.IsBinary || file.AddedLineNumbers is not { Count: > 0 })
                {
                    files.Add(file);
                    continue;
                }
                var blame = await RunTextGitAsync(repositoryPath, ["blame", "--no-textconv", "--root", "--first-parent", "--line-porcelain", head, "--", file.RelativePath],
                    cancellationToken, maximumOutputBytes: MaximumPatchBytes);
                if (blame.OutputLimitExceeded)
                    throw new InvalidOperationException("Selected-commit attribution exceeded the capture limit; run separate commit checks.");
                EnsureSucceeded(blame, "attribute selected additions", repositoryPath);
                var additions = file.AddedLineNumbers.ToHashSet();
                var builder = new AddedCodeBuilder();
                string? origin = null;
                var number = 0;
                using var reader = new StringReader(blame.StdOut);
                while (reader.ReadLine() is { } line)
                {
                    if (line.StartsWith('\t'))
                    {
                        if (origin is not null && resolved.Contains(origin) && additions.Contains(number))
                            builder.Add(line[1..], number);
                        origin = null;
                        continue;
                    }
                    if (origin is not null) continue;
                    var fields = line.Split(' ');
                    if (fields.Length >= 3 && JobCheckScope.IsSha(fields[0])
                        && int.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out number))
                        origin = fields[0];
                }
                var added = builder.Build();
                files.Add(file with { AddedContent = added.Content, AddedLineNumbers = added.SourceLineNumbers });
            }
            snapshot = snapshot with { Files = files };
        }
        return snapshot with { CheckIdentity = new(baseline, head, resolved.Order(StringComparer.Ordinal).ToArray()) };
    }
}
