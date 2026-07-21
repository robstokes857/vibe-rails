using System.Diagnostics;
using System.Globalization;
using System.Text;
using MintLint;
using Serilog;

namespace VibeRails.Services.GitPreflight;

public sealed class GitStagedSnapshotProvider : IGitStagedSnapshotProvider, IGitWorkingTreeSnapshotProvider
{
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(15);
    private const int MaximumTextFileBytes = 5 * 1024 * 1024;

    /// <summary>
    /// Ceiling on the file content one working-tree snapshot will hold in memory at once.
    ///
    /// The staged snapshot is implicitly bounded by what the user chose to stage. The
    /// working-tree snapshot has no such limit — it reads every changed and untracked file —
    /// so without a budget a repository carrying a few hundred large unignored files would
    /// allocate all of them simultaneously.
    /// </summary>
    private const long MaximumSnapshotBytes = 64L * 1024 * 1024;
    private readonly long _maximumUnpushedSnapshotBytes;
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    /// <summary>Index mode of a submodule (gitlink) entry.</summary>
    private const string GitLinkMode = "160000";

    public GitStagedSnapshotProvider()
        : this(MaximumSnapshotBytes)
    {
    }

    internal GitStagedSnapshotProvider(long maximumUnpushedSnapshotBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumUnpushedSnapshotBytes);
        _maximumUnpushedSnapshotBytes = maximumUnpushedSnapshotBytes;
    }

    public async Task<GitStagedSnapshot> CaptureAsync(
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var rootResult = await RunTextGitAsync(
            workingDirectory,
            ["rev-parse", "--show-toplevel"],
            cancellationToken);
        EnsureSucceeded(rootResult, "locate the Git repository", workingDirectory);
        var repositoryPath = Path.GetFullPath(rootResult.StdOut.Trim());

        // Freeze the moving index into a tree object once, then perform every subsequent read from
        // immutable objects. VCA, MintLint, and queued jobs therefore share exactly one identity.
        var identity = await CaptureStagedIdentityAsync(repositoryPath, cancellationToken);
        var stagedTreeEntries = await ReadTreeEntriesAsync(
            repositoryPath,
            identity.StagedTree,
            cancellationToken);
        var baseTreeEntries = await ReadTreeEntriesAsync(
            repositoryPath,
            identity.BaseTree,
            cancellationToken);
        var stagedByPath = stagedTreeEntries.ToDictionary(entry => entry.RelativePath, PathComparer);
        var baseByPath = baseTreeEntries.ToDictionary(entry => entry.RelativePath, PathComparer);
        var trackedFiles = stagedTreeEntries
            .Where(entry => entry.Type == "blob" && entry.Mode != GitLinkMode)
            .Select(entry => entry.RelativePath)
            .Distinct(PathComparer)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

        var statusResult = await RunTextGitAsync(
            repositoryPath,
            [
                "--no-pager", "diff", "--name-status", "-z", "--find-renames",
                identity.BaseTree, identity.StagedTree, "--"
            ],
            cancellationToken);
        EnsureSucceeded(statusResult, "read staged file status", repositoryPath);

        var stagedEntries = ParseNameStatus(statusResult.StdOut);
        var changedLines = await ReadChangedLineCountsAsync(
            repositoryPath,
            identity.BaseTree,
            identity.StagedTree,
            cancellationToken);
        var files = new List<GitStagedFileSnapshot>(stagedEntries.Count);
        var contentBudget = new SnapshotMemoryBudget(long.MaxValue);
        var textByObject = new Dictionary<string, BlobTextResult>(StringComparer.Ordinal);

        async Task<BlobTextResult> ReadTreeTextAsync(GitTreeEntry treeEntry)
        {
            if (textByObject.TryGetValue(treeEntry.ObjectId, out var cached))
                return cached;
            var loaded = await TryReadTextBlobAsync(
                repositoryPath,
                treeEntry.ObjectId,
                treeEntry.Size,
                contentBudget,
                cancellationToken);
            textByObject[treeEntry.ObjectId] = loaded;
            return loaded;
        }

        foreach (var entry in stagedEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GitTreeEntry? stagedTreeEntry = null;
            var existsInIndex = entry.Kind != GitStagedChangeKind.Deleted
                && stagedByPath.TryGetValue(entry.RelativePath, out stagedTreeEntry);
            string? content = null;
            var isBinary = !existsInIndex;

            if (existsInIndex && stagedTreeEntry!.Mode == GitLinkMode)
            {
                isBinary = true;
            }
            else if (existsInIndex && stagedTreeEntry!.Type == "blob")
            {
                var blob = await ReadTreeTextAsync(stagedTreeEntry);
                content = blob.Content;
                isBinary = blob.IsBinary;
            }

            // The committed version of the file, so consumers can tell newly introduced
            // problems apart from ones the file already had. Modified/renamed files only;
            // added and copied paths had no code at this location before.
            string? previousContent = null;
            if (!isBinary && entry.Kind is GitStagedChangeKind.Modified or GitStagedChangeKind.Renamed)
            {
                var previousPath = entry.PreviousRelativePath ?? entry.RelativePath;
                if (baseByPath.TryGetValue(previousPath, out var baseTreeEntry)
                    && baseTreeEntry.Type == "blob"
                    && baseTreeEntry.Mode != GitLinkMode)
                {
                    var previousBlob = await ReadTreeTextAsync(baseTreeEntry);
                    if (!previousBlob.IsBinary)
                        previousContent = previousBlob.Content;
                }
            }

            changedLines.TryGetValue(entry.RelativePath, out var lineCount);
            files.Add(new GitStagedFileSnapshot(
                entry.RelativePath,
                ToFullPath(repositoryPath, entry.RelativePath),
                entry.Kind,
                existsInIndex,
                isBinary,
                lineCount,
                content,
                entry.PreviousRelativePath,
                previousContent));
        }

        var agentFiles = new List<GitIndexTextFile>();
        foreach (var entry in stagedTreeEntries.Where(entry => entry.Type == "blob" && IsAgentFile(entry.RelativePath)))
        {
            var agent = await ReadTreeTextAsync(entry);
            if (!agent.IsBinary && agent.Content is not null)
                agentFiles.Add(new GitIndexTextFile(entry.RelativePath, agent.Content));
        }

        return new GitStagedSnapshot(
            repositoryPath,
            files,
            agentFiles,
            trackedFiles,
            StagedIdentity: identity);
    }

    /// <summary>
    /// Captures the current working tree relative to HEAD for the interactive code analyzer.
    /// This deliberately does not replace <see cref="CaptureAsync"/>: commit preflight must
    /// continue to validate the exact staged index snapshot.
    /// </summary>
    public async Task<GitStagedSnapshot> CaptureWorkingTreeAsync(
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var rootResult = await RunTextGitAsync(
            workingDirectory,
            ["rev-parse", "--show-toplevel"],
            cancellationToken);
        EnsureSucceeded(rootResult, "locate the Git repository", workingDirectory);
        var repositoryPath = Path.GetFullPath(rootResult.StdOut.Trim());

        var indexEntries = await ReadIndexEntriesAsync(repositoryPath, cancellationToken);
        var gitLinkPaths = CollectGitLinkPaths(indexEntries);
        var trackedFiles = ToTrackedFiles(indexEntries);
        var hasHead = await HasHeadAsync(repositoryPath, cancellationToken);
        var changedEntries = hasHead
            ? await ReadWorkingTreeStatusAsync(repositoryPath, cancellationToken)
            : trackedFiles.Select(path => new StagedStatusEntry(
                path,
                GitStagedChangeKind.Added,
                PreviousRelativePath: null)).ToList();
        var changedLines = hasHead
            ? await ReadWorkingTreeChangedLineCountsAsync(repositoryPath, cancellationToken)
            : new Dictionary<string, int?>(PathComparer);

        var untrackedResult = await RunTextGitAsync(
            repositoryPath,
            ["ls-files", "--others", "--exclude-standard", "-z"],
            cancellationToken);
        EnsureSucceeded(untrackedResult, "read untracked files", repositoryPath);

        var entryIndexByPath = new Dictionary<string, int>(PathComparer);
        for (var i = 0; i < changedEntries.Count; i++)
        {
            entryIndexByPath[changedEntries[i].RelativePath] = i;
        }

        var untrackedPaths = SplitNull(untrackedResult.StdOut)
            .Select(NormalizePath)
            .Where(path => path.Length > 0)
            .ToList();

        foreach (var path in untrackedPaths)
        {
            var existing = entryIndexByPath.TryGetValue(path, out var existingIndex);
            var entry = new StagedStatusEntry(
                path,
                existing && changedEntries[existingIndex].Kind == GitStagedChangeKind.Deleted
                    ? GitStagedChangeKind.Modified
                    : GitStagedChangeKind.Added,
                PreviousRelativePath: null);
            if (existing)
            {
                // A path can be staged for deletion and then recreated as an untracked
                // working-tree file. Prefer the file that actually exists and analyze it.
                changedEntries[existingIndex] = entry;
            }
            else
            {
                entryIndexByPath[path] = changedEntries.Count;
                changedEntries.Add(entry);
            }
        }

        var files = new List<GitStagedFileSnapshot>(changedEntries.Count);
        var pathGuard = new WorkingTreePathGuard(repositoryPath);
        var contentBytesRead = 0L;
        var skippedForBudget = 0;
        foreach (var entry in changedEntries
            .GroupBy(item => item.RelativePath, PathComparer)
            .Select(group => group.First())
            .OrderBy(item => item.RelativePath, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fullPath = ToFullPath(repositoryPath, entry.RelativePath);
            // A submodule (gitlink) path is a directory whose "content" is a commit
            // pointer; there is nothing to read here or from HEAD.
            var isGitLink = gitLinkPaths.Contains(entry.RelativePath);
            var existsInWorkingTree = !isGitLink
                && entry.Kind != GitStagedChangeKind.Deleted
                && pathGuard.IsReadableRegularFile(fullPath);
            string? content = null;
            var isBinary = isGitLink;
            if (existsInWorkingTree)
            {
                var length = new FileInfo(fullPath).Length;
                if (contentBytesRead + length > MaximumSnapshotBytes)
                {
                    // Out of budget: mark it unanalyzable, the same way an oversized file is
                    // reported, rather than reading it and blowing the ceiling.
                    skippedForBudget++;
                    isBinary = true;
                }
                else
                {
                    (content, isBinary) = await ReadWorkingTreeContentAsync(fullPath, cancellationToken);
                    contentBytesRead += length;
                }
            }

            string? previousContent = null;
            if (!isBinary
                && hasHead
                && entry.Kind is GitStagedChangeKind.Modified or GitStagedChangeKind.Renamed)
            {
                var previousPath = entry.PreviousRelativePath ?? entry.RelativePath;
                var headBlob = await TryReadHeadBlobAsync(repositoryPath, previousPath, cancellationToken);
                if (headBlob != null)
                {
                    previousContent = DecodeText(headBlob, out var previousBinary);
                    if (previousBinary)
                    {
                        previousContent = null;
                    }
                }
            }

            // Untracked files have no diff against HEAD, so every line in them is new.
            if (!changedLines.TryGetValue(entry.RelativePath, out var lineCount) && content != null)
            {
                lineCount = CountLines(content);
            }

            // ExistsInIndex is the legacy snapshot field consumed by preflight steps. For
            // this working-tree snapshot it means a current, analyzable file exists; this
            // is intentionally true for untracked files and false for deleted paths.
            files.Add(new GitStagedFileSnapshot(
                entry.RelativePath,
                fullPath,
                entry.Kind,
                existsInWorkingTree,
                isBinary,
                lineCount,
                content,
                entry.PreviousRelativePath,
                previousContent));
        }

        if (skippedForBudget > 0)
        {
            // Never let a bounded scan read as a complete one.
            Log.Warning(
                "[GitPreflight] Working-tree snapshot reached its {BudgetBytes} byte content budget; "
                + "{SkippedCount} file(s) were left unanalyzed.",
                MaximumSnapshotBytes,
                skippedForBudget);
        }

        var agentFiles = await ReadWorkingTreeAgentFilesAsync(
            repositoryPath,
            trackedFiles,
            untrackedPaths,
            pathGuard,
            cancellationToken);
        return new GitStagedSnapshot(repositoryPath, files, agentFiles, trackedFiles);
    }

    /// <summary>
    /// Captures the diff between the current branch's upstream tracking ref and HEAD —
    /// i.e. every file touched by commits the user has made but not yet pushed. The
    /// working tree is intentionally ignored: this scan answers "what did my unpushed
    /// commits change", not "what is on disk right now".
    ///
    /// Throws <see cref="InvalidOperationException"/> with a user-actionable message when
    /// the branch has no upstream (e.g. a brand-new branch that has never been pushed).
    /// The caller maps that to a friendly API response.
    /// </summary>
    public async Task<GitStagedSnapshot> CaptureUnpushedAsync(
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var rootResult = await RunTextGitAsync(
            workingDirectory,
            ["rev-parse", "--show-toplevel"],
            cancellationToken);
        EnsureSucceeded(rootResult, "locate the Git repository", workingDirectory);
        var repositoryPath = Path.GetFullPath(rootResult.StdOut.Trim());

        // Resolve @{upstream}. `--symbolic-full-name` returns e.g. "origin/main"; without
        // it we'd get the SHA, which works for diffs but produces worse error messages.
        var upstreamResult = await RunTextGitAsync(
            repositoryPath,
            ["rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{upstream}"],
            cancellationToken);
        if (upstreamResult.ExitCode != 0 || string.IsNullOrWhiteSpace(upstreamResult.StdOut)
            || upstreamResult.StdOut.Trim().Equals("@{upstream}", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "This branch has no upstream tracking branch. Push it first with "
                + "`git push -u origin <branch>` so VibeRails can diff against it.");
        }
        var upstreamRef = upstreamResult.StdOut.Trim();

        // Pin both moving refs before reading any tree or blob. Every command below uses these
        // immutable object ids, so a concurrent commit/fetch cannot splice two revisions together.
        var headResult = await RunTextGitAsync(
            repositoryPath,
            ["rev-parse", "--verify", "HEAD^{commit}"],
            cancellationToken);
        EnsureSucceeded(headResult, "resolve HEAD", repositoryPath);
        var headCommit = headResult.StdOut.Trim();

        var upstreamCommitResult = await RunTextGitAsync(
            repositoryPath,
            ["rev-parse", "--verify", $"{upstreamRef}^{{commit}}"],
            cancellationToken);
        EnsureSucceeded(upstreamCommitResult, $"resolve upstream branch {upstreamRef}", repositoryPath);
        var upstreamCommit = upstreamCommitResult.StdOut.Trim();

        // "What did my unpushed commits change" is a fork-point question: diff from the merge base of
        // upstream and HEAD, not from the (possibly diverged) upstream tip. Two-dot `upstream..HEAD`
        // misattributes commits the upstream gained independently — they show up reversed as "your"
        // deletions — and pins the baseline to the upstream tip. Three-dot `upstream...HEAD` is
        // defined as `merge-base(upstream, HEAD)..HEAD`; resolve the merge base explicitly so
        // name-status, numstat, and the previous-content blobs all read from the same fork point.
        var mergeBaseResult = await RunTextGitAsync(
            repositoryPath,
            ["merge-base", upstreamCommit, headCommit],
            cancellationToken);
        EnsureSucceeded(mergeBaseResult, $"find the merge base of {upstreamRef} and HEAD", repositoryPath);
        var mergeBaseRef = mergeBaseResult.StdOut.Trim();

        // This scope represents committed state, so enumerate HEAD's tree rather than the index.
        // `-l` supplies each blob's size before any content is read.
        var headEntries = await ReadTreeEntriesAsync(repositoryPath, headCommit, cancellationToken);
        var headByPath = headEntries.ToDictionary(entry => entry.RelativePath, PathComparer);
        var trackedFiles = headEntries
            .Where(entry => entry.Type == "blob" && entry.Mode != GitLinkMode)
            .Select(entry => entry.RelativePath)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

        var statusResult = await RunTextGitAsync(
            repositoryPath,
            ["--no-pager", "diff", "--name-status", "-z", "--find-renames", $"{mergeBaseRef}..{headCommit}"],
            cancellationToken);
        EnsureSucceeded(statusResult, $"read unpushed file status ({upstreamRef}...HEAD)", repositoryPath);

        var changedEntries = ParseNameStatus(statusResult.StdOut);
        var changedLines = await ReadUnpushedChangedLineCountsAsync(
            repositoryPath,
            mergeBaseRef,
            headCommit,
            cancellationToken);

        var budget = new SnapshotMemoryBudget(_maximumUnpushedSnapshotBytes);
        var headTextByObject = new Dictionary<string, BlobTextResult>(StringComparer.Ordinal);

        async Task<BlobTextResult> ReadHeadTextAsync(GitTreeEntry entry)
        {
            if (headTextByObject.TryGetValue(entry.ObjectId, out var cached))
            {
                return cached;
            }

            var loaded = await TryReadTextBlobAsync(
                repositoryPath,
                entry.ObjectId,
                entry.Size,
                budget,
                cancellationToken);
            headTextByObject[entry.ObjectId] = loaded;
            return loaded;
        }

        // Rules are part of the snapshot's meaning, so reserve memory for HEAD's agent files before
        // lower-priority baselines and impact-only sources consume the aggregate budget.
        var agentFiles = new List<GitIndexTextFile>();
        foreach (var entry in headEntries.Where(entry => entry.Type == "blob" && IsAgentFile(entry.RelativePath)))
        {
            var agent = await ReadHeadTextAsync(entry);
            if (!agent.IsBinary && agent.Content is not null)
            {
                agentFiles.Add(new GitIndexTextFile(entry.RelativePath, agent.Content));
            }
        }

        var files = new List<GitStagedFileSnapshot>(changedEntries.Count);
        foreach (var entry in changedEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // The "current" version is the file at HEAD. Deleted files have no HEAD blob
            // (the path no longer exists in the tree) — record them as unanalyzable so
            // they still appear in the snapshot count but contribute nothing to the scan.
            GitTreeEntry? headEntry = null;
            var existsInHead = entry.Kind != GitStagedChangeKind.Deleted
                && headByPath.TryGetValue(entry.RelativePath, out headEntry)
                && headEntry.Type == "blob"
                && headEntry.Mode != GitLinkMode;
            string? content = null;
            var isBinary = !existsInHead;
            if (existsInHead && headEntry is not null)
            {
                var headBlob = await ReadHeadTextAsync(headEntry);
                content = headBlob.Content;
                isBinary = headBlob.IsBinary;
            }

            // The "previous" version is the file at the merge base (fork point) of upstream and HEAD,
            // matching the three-dot diff above. Added files have no blob there (the path didn't
            // exist before this branch's commits) — the scan treats them as newly introduced. If the
            // merge base couldn't be resolved (unrelated histories), there is no baseline and every
            // changed file reads as newly introduced.
            string? previousContent = null;
            if (!isBinary
                && entry.Kind is GitStagedChangeKind.Modified or GitStagedChangeKind.Renamed)
            {
                var previousPath = entry.PreviousRelativePath ?? entry.RelativePath;
                var baseBlob = await TryReadTextBlobAsync(
                    repositoryPath,
                    $"{mergeBaseRef}:./{previousPath}",
                    knownSize: null,
                    budget,
                    cancellationToken);
                if (!baseBlob.IsBinary)
                {
                    previousContent = baseBlob.Content;
                }
            }

            changedLines.TryGetValue(entry.RelativePath, out var lineCount);
            files.Add(new GitStagedFileSnapshot(
                entry.RelativePath,
                ToFullPath(repositoryPath, entry.RelativePath),
                entry.Kind,
                existsInHead,
                isBinary,
                lineCount,
                content,
                entry.PreviousRelativePath,
                previousContent));
        }

        // Impact ranking also consumes immutable HEAD contents. Reuse blobs retained above and fill
        // the remaining aggregate budget with supported source files from the HEAD tree.
        var impactFiles = new List<GitIndexTextFile>();
        foreach (var entry in headEntries.Where(entry =>
                     entry.Type == "blob" && MintLintAnalyzer.SupportsFile(entry.RelativePath)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = await ReadHeadTextAsync(entry);
            if (!source.IsBinary && source.Content is not null)
            {
                impactFiles.Add(new GitIndexTextFile(entry.RelativePath, source.Content));
            }
        }

        if (budget.SkippedFileCount > 0)
        {
            Log.Warning(
                "[GitPreflight] Unpushed snapshot reached its {BudgetBytes} byte content budget; "
                + "{SkippedCount} blob(s) were left unanalyzed.",
                _maximumUnpushedSnapshotBytes,
                budget.SkippedFileCount);
        }

        return new GitStagedSnapshot(repositoryPath, files, agentFiles, trackedFiles, impactFiles);
    }

    private static async Task<Dictionary<string, int?>> ReadUnpushedChangedLineCountsAsync(
        string repositoryPath,
        string mergeBaseRef,
        string headCommit,
        CancellationToken cancellationToken)
    {
        var result = await RunTextGitAsync(
            repositoryPath,
            ["--no-pager", "diff", "--numstat", "-z", $"{mergeBaseRef}..{headCommit}"],
            cancellationToken);
        EnsureSucceeded(result, "count unpushed changed lines", repositoryPath);
        return ParseChangedLineCounts(result.StdOut);
    }

    /// <summary>
    /// Rules for the working-tree snapshot come from the working tree itself: an unstaged
    /// AGENTS.md edit — or a brand-new untracked AGENTS.md — applies to this preview, even
    /// though the commit hooks keep reading rules from the index. By the same principle, an
    /// AGENTS.md deleted from the working tree contributes no rules here.
    /// </summary>
    private static async Task<IReadOnlyList<GitIndexTextFile>> ReadWorkingTreeAgentFilesAsync(
        string repositoryPath,
        IReadOnlyList<string> trackedFiles,
        IReadOnlyList<string> untrackedFiles,
        WorkingTreePathGuard pathGuard,
        CancellationToken cancellationToken)
    {
        var paths = trackedFiles
            .Concat(untrackedFiles)
            .Where(IsAgentFile)
            .Distinct(PathComparer)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();
        var results = new List<GitIndexTextFile>(paths.Count);

        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fullPath = ToFullPath(repositoryPath, path);
            if (!pathGuard.IsReadableRegularFile(fullPath))
            {
                continue;
            }

            var (content, isBinary) = await ReadWorkingTreeContentAsync(fullPath, cancellationToken);
            if (!isBinary && content != null)
            {
                results.Add(new GitIndexTextFile(path, content));
            }
        }

        return results;
    }

    private static async Task<bool> HasHeadAsync(
        string repositoryPath,
        CancellationToken cancellationToken)
    {
        var result = await RunTextGitAsync(
            repositoryPath,
            ["rev-parse", "--verify", "HEAD"],
            cancellationToken);
        return result.ExitCode == 0 && !result.TimedOut;
    }

    private static async Task<List<StagedStatusEntry>> ReadWorkingTreeStatusAsync(
        string repositoryPath,
        CancellationToken cancellationToken)
    {
        var result = await RunTextGitAsync(
            repositoryPath,
            ["--no-pager", "diff", "HEAD", "--name-status", "-z", "--find-renames"],
            cancellationToken);
        EnsureSucceeded(result, "read working tree file status", repositoryPath);
        return ParseNameStatus(result.StdOut);
    }

    private static async Task<Dictionary<string, int?>> ReadWorkingTreeChangedLineCountsAsync(
        string repositoryPath,
        CancellationToken cancellationToken)
    {
        var result = await RunTextGitAsync(
            repositoryPath,
            ["--no-pager", "diff", "HEAD", "--numstat", "-z"],
            cancellationToken);
        EnsureSucceeded(result, "count working tree changed lines", repositoryPath);
        return ParseChangedLineCounts(result.StdOut);
    }

    /// <summary>
    /// Decides which working-tree paths are safe to read.
    ///
    /// The staged snapshot reads blobs out of the object store, where a symlink is stored as
    /// its link text and can never leak its target. This snapshot reads the filesystem
    /// instead, and <see cref="File.ReadAllBytesAsync(string, CancellationToken)"/> follows
    /// links — so an untracked <c>leak.cs</c> pointing at <c>~/.ssh/id_rsa</c> would be read
    /// and handed back to the client as a source excerpt.
    ///
    /// Checking the file alone is not enough: Git happily lists <c>linkdir/leak.cs</c> where
    /// <c>linkdir</c> is a junction out of the repository, and that file is not itself a
    /// reparse point. So every directory between the file and the repository root is checked
    /// too, and the walk stops at the root — a repository that legitimately sits under a
    /// junction stays readable.
    ///
    /// Limitation: hardlinks are not detected (they carry no reparse point and no link
    /// target), so a hardlinked file inside the tree can still be read. Symlinks and
    /// junctions — the practical exfiltration vectors — are what this guards against.
    /// </summary>
    // Internal (not private): the code-analyzer source endpoint reuses the same guard so a
    // requested path gets exactly the symlink/junction containment checks a scan would apply.
    internal sealed class WorkingTreePathGuard(string repositoryPath)
    {
        private readonly string _repositoryPath = Normalize(repositoryPath);
        private readonly Dictionary<string, bool> _directoryIsContained = new(PathComparer);

        public bool IsReadableRegularFile(string fullPath)
        {
            FileInfo info;
            try
            {
                info = new FileInfo(fullPath);
                if (!info.Exists
                    || info.LinkTarget is not null
                    || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    return false;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }

            return IsContained(Path.GetDirectoryName(fullPath));
        }

        private bool IsContained(string? directory)
        {
            if (string.IsNullOrEmpty(directory))
            {
                // Walked past the drive root without reaching the repository.
                return false;
            }

            var normalized = Normalize(directory);
            if (_directoryIsContained.TryGetValue(normalized, out var cached))
            {
                return cached;
            }

            bool contained;
            if (PathComparer.Equals(normalized, _repositoryPath))
            {
                contained = true;
            }
            else
            {
                try
                {
                    var info = new DirectoryInfo(normalized);
                    contained = info.Exists
                        && info.LinkTarget is null
                        && !info.Attributes.HasFlag(FileAttributes.ReparsePoint)
                        && IsContained(Path.GetDirectoryName(normalized));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    contained = false;
                }
            }

            _directoryIsContained[normalized] = contained;
            return contained;
        }

        private static string Normalize(string path) =>
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static async Task<(string? Content, bool IsBinary)> ReadWorkingTreeContentAsync(
        string fullPath,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(fullPath);
        if (info.Length > MaximumTextFileBytes)
        {
            return (null, true);
        }

        var bytes = await File.ReadAllBytesAsync(fullPath, cancellationToken);
        var content = DecodeText(bytes, out var isBinary);
        return (content, isBinary);
    }

    private static int CountLines(string content)
    {
        if (content.Length == 0)
        {
            return 0;
        }

        var count = content.Count(character => character == '\n');
        return content[^1] == '\n' ? count : checked(count + 1);
    }

    private static async Task<IReadOnlyList<GitIndexEntry>> ReadIndexEntriesAsync(
        string repositoryPath,
        CancellationToken cancellationToken)
    {
        var indexResult = await RunTextGitAsync(
            repositoryPath,
            ["ls-files", "--stage", "-z"],
            cancellationToken);
        EnsureSucceeded(indexResult, "read the Git index", repositoryPath);

        // Each record is "<mode> <object> <stage>\t<path>". The mode distinguishes
        // regular files from gitlinks, which have no blob to read in this repository.
        var entries = new List<GitIndexEntry>();
        foreach (var record in SplitNull(indexResult.StdOut))
        {
            var tabIndex = record.IndexOf('\t');
            var spaceIndex = record.IndexOf(' ');
            if (tabIndex <= 0 || spaceIndex <= 0 || spaceIndex > tabIndex)
            {
                continue;
            }

            var path = NormalizePath(record[(tabIndex + 1)..]);
            if (path.Length > 0)
            {
                entries.Add(new GitIndexEntry(path, record[..spaceIndex]));
            }
        }

        return entries;
    }

    private static async Task<IReadOnlyList<GitTreeEntry>> ReadTreeEntriesAsync(
        string repositoryPath,
        string treeish,
        CancellationToken cancellationToken)
    {
        var treeResult = await RunTextGitAsync(
            repositoryPath,
            ["ls-tree", "-r", "-z", "-l", "--full-tree", treeish],
            cancellationToken);
        EnsureSucceeded(treeResult, $"read the Git tree {treeish}", repositoryPath);

        // With -l each record is "<mode> <type> <object> <size>\t<path>". Blob size is
        // therefore known before content is requested; gitlinks have type "commit" and no size.
        var entries = new List<GitTreeEntry>();
        foreach (var record in SplitNull(treeResult.StdOut))
        {
            var tabIndex = record.IndexOf('\t');
            if (tabIndex <= 0)
            {
                continue;
            }

            var fields = record[..tabIndex].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var path = NormalizePath(record[(tabIndex + 1)..]);
            if (fields.Length < 4 || path.Length == 0)
            {
                continue;
            }

            long? size = long.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out var parsedSize)
                ? parsedSize
                : null;
            entries.Add(new GitTreeEntry(path, fields[0], fields[1], fields[2], size));
        }

        return entries;
    }

    private static async Task<GitStagedSnapshotIdentity> CaptureStagedIdentityAsync(
        string repositoryPath,
        CancellationToken cancellationToken)
    {
        var headResult = await RunTextGitAsync(
            repositoryPath,
            ["rev-parse", "--verify", "HEAD^{commit}"],
            cancellationToken);

        string? baseCommit = null;
        string baseTree;
        if (headResult.ExitCode == 0 && !headResult.TimedOut)
        {
            baseCommit = headResult.StdOut.Trim();
            if (!IsObjectId(baseCommit))
                throw new InvalidOperationException("Git returned an invalid staged snapshot base commit id.");
            var baseTreeResult = await RunTextGitAsync(
                repositoryPath,
                ["rev-parse", "--verify", $"{baseCommit}^{{tree}}"],
                cancellationToken);
            EnsureSucceeded(baseTreeResult, "resolve the staged snapshot base tree", repositoryPath);
            baseTree = baseTreeResult.StdOut.Trim();
        }
        else
        {
            if (headResult.TimedOut)
                throw new TimeoutException("Git timed out while resolving the staged snapshot base commit.");

            // Distinguish a legitimate unborn branch from a corrupt/detached HEAD before treating
            // the base as an empty tree.
            var symbolicHead = await RunTextGitAsync(
                repositoryPath,
                ["symbolic-ref", "-q", "HEAD"],
                cancellationToken);
            EnsureSucceeded(symbolicHead, "verify the unborn branch", repositoryPath);

            var emptyTreeResult = await RunTextGitAsync(
                repositoryPath,
                ["mktree"],
                cancellationToken,
                standardInput: ReadOnlyMemory<byte>.Empty);
            EnsureSucceeded(emptyTreeResult, "create the empty initial-commit tree", repositoryPath);
            baseTree = emptyTreeResult.StdOut.Trim();
        }

        if (!IsObjectId(baseTree))
            throw new InvalidOperationException("Git returned an invalid staged snapshot base tree id.");

        var stagedTreeResult = await RunTextGitAsync(
            repositoryPath,
            ["write-tree"],
            cancellationToken);
        EnsureSucceeded(stagedTreeResult, "freeze the staged index tree", repositoryPath);
        var stagedTree = stagedTreeResult.StdOut.Trim();
        if (!IsObjectId(stagedTree))
            throw new InvalidOperationException("Git returned an invalid staged index tree id.");

        return new GitStagedSnapshotIdentity(baseCommit, baseTree, stagedTree);
    }

    private static async Task<BlobTextResult> TryReadTextBlobAsync(
        string repositoryPath,
        string objectSpec,
        long? knownSize,
        SnapshotMemoryBudget budget,
        CancellationToken cancellationToken)
    {
        var size = knownSize;
        if (size is null)
        {
            var sizeResult = await RunTextGitAsync(
                repositoryPath,
                ["cat-file", "-s", objectSpec],
                cancellationToken);
            if (sizeResult.ExitCode != 0
                || sizeResult.TimedOut
                || !long.TryParse(
                    sizeResult.StdOut.Trim(),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var parsedSize))
            {
                return BlobTextResult.Binary;
            }

            size = parsedSize;
        }

        if (size < 0 || size > MaximumTextFileBytes)
        {
            return BlobTextResult.Binary;
        }

        if (!budget.TryReserve(size.Value))
        {
            return BlobTextResult.Binary;
        }

        var result = await RunBinaryGitAsync(
            repositoryPath,
            ["cat-file", "blob", objectSpec],
            cancellationToken,
            MaximumTextFileBytes);
        if (result.ExitCode != 0 || result.TimedOut || result.OutputLimitExceeded)
        {
            budget.Release(size.Value);
            return BlobTextResult.Binary;
        }

        var content = DecodeText(result.Bytes, out var binary);
        if (binary || content is null)
        {
            budget.Release(size.Value);
            return BlobTextResult.Binary;
        }

        return new BlobTextResult(content, IsBinary: false);
    }

    private static HashSet<string> CollectGitLinkPaths(IReadOnlyList<GitIndexEntry> entries) =>
        entries
            .Where(entry => entry.Mode == GitLinkMode)
            .Select(entry => entry.RelativePath)
            .ToHashSet(PathComparer);

    /// <summary>
    /// Tracked file paths, excluding gitlinks: a submodule entry is a directory pointer,
    /// not a readable file, so consumers that iterate tracked files must never see it.
    /// </summary>
    private static IReadOnlyList<string> ToTrackedFiles(IReadOnlyList<GitIndexEntry> entries) =>
        entries
            .Where(entry => entry.Mode != GitLinkMode)
            .Select(entry => entry.RelativePath)
            .Distinct(PathComparer)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

    private static async Task<Dictionary<string, int?>> ReadChangedLineCountsAsync(
        string repositoryPath,
        string baseTree,
        string stagedTree,
        CancellationToken cancellationToken)
    {
        var result = await RunTextGitAsync(
            repositoryPath,
            ["--no-pager", "diff", "--numstat", "-z", baseTree, stagedTree, "--"],
            cancellationToken);
        EnsureSucceeded(result, "count staged changed lines", repositoryPath);

        return ParseChangedLineCounts(result.StdOut);
    }

    private static Dictionary<string, int?> ParseChangedLineCounts(string output)
    {
        var counts = new Dictionary<string, int?>(PathComparer);
        var records = SplitNull(output).ToList();
        for (var i = 0; i < records.Count; i++)
        {
            var record = records[i];
            var firstTab = record.IndexOf('\t');
            var secondTab = firstTab < 0 ? -1 : record.IndexOf('\t', firstTab + 1);
            if (firstTab < 0 || secondTab < 0)
            {
                continue;
            }

            var path = record[(secondTab + 1)..];
            // With -z, a rename is emitted as "added<TAB>deleted<TAB><NUL>old<NUL>new<NUL>".
            if (path.Length == 0 && i + 2 < records.Count)
            {
                i++;
                path = records[++i];
            }

            path = NormalizePath(path);
            if (path.Length == 0)
            {
                continue;
            }

            counts[path] = int.TryParse(record[..firstTab], out var added)
                && int.TryParse(record[(firstTab + 1)..secondTab], out var deleted)
                    ? checked(added + deleted)
                    : null;
        }

        return counts;
    }

    private static List<StagedStatusEntry> ParseNameStatus(string output)
    {
        var fields = SplitNull(output).ToList();
        var entries = new List<StagedStatusEntry>();
        for (var i = 0; i < fields.Count; i++)
        {
            var status = fields[i];
            if (status.Length == 0 || i + 1 >= fields.Count)
            {
                break;
            }

            var kind = ToChangeKind(status[0]);
            var firstPath = NormalizePath(fields[++i]);
            if (kind is GitStagedChangeKind.Renamed or GitStagedChangeKind.Copied)
            {
                if (i + 1 >= fields.Count)
                {
                    break;
                }

                var newPath = NormalizePath(fields[++i]);
                entries.Add(new StagedStatusEntry(newPath, kind, firstPath));
            }
            else
            {
                entries.Add(new StagedStatusEntry(firstPath, kind, null));
            }
        }

        return entries;
    }

    private static GitStagedChangeKind ToChangeKind(char status) => status switch
    {
        'A' => GitStagedChangeKind.Added,
        'M' or 'T' => GitStagedChangeKind.Modified,
        'D' => GitStagedChangeKind.Deleted,
        'R' => GitStagedChangeKind.Renamed,
        'C' => GitStagedChangeKind.Copied,
        'U' => GitStagedChangeKind.Unmerged,
        _ => GitStagedChangeKind.Unknown
    };

    /// <summary>
    /// Reads the committed (HEAD) version of a path. Returns null instead of throwing:
    /// an unborn branch or a path new to this commit simply has no baseline.
    /// </summary>
    private static async Task<byte[]?> TryReadHeadBlobAsync(
        string repositoryPath,
        string relativePath,
        CancellationToken cancellationToken)
    {
        var objectSpec = $"HEAD:./{relativePath}";
        var sizeResult = await RunTextGitAsync(
            repositoryPath,
            ["cat-file", "-s", objectSpec],
            cancellationToken);
        if (sizeResult.ExitCode != 0
            || sizeResult.TimedOut
            || !long.TryParse(
                sizeResult.StdOut.Trim(),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var size)
            || size < 0
            || size > MaximumTextFileBytes)
        {
            return null;
        }

        var result = await RunBinaryGitAsync(
            repositoryPath,
            ["cat-file", "blob", objectSpec],
            cancellationToken,
            MaximumTextFileBytes);
        return result.ExitCode == 0 && !result.TimedOut && !result.OutputLimitExceeded
            ? result.Bytes
            : null;
    }

    /// <summary>
    /// Reads the committed (HEAD) bytes of a repo-relative path; null when absent. Used by the
    /// code-analyzer source endpoint so an unpushed scan shows the exact revision it scored (HEAD),
    /// not the working-tree file.
    /// </summary>
    internal static Task<byte[]?> TryReadHeadBlobPublicAsync(
        string repositoryPath,
        string relativePath,
        CancellationToken cancellationToken) =>
        TryReadHeadBlobAsync(repositoryPath, relativePath, cancellationToken);

    internal static string? DecodeText(byte[] bytes, out bool isBinary)
    {
        if (bytes.Length > MaximumTextFileBytes || Array.IndexOf(bytes, (byte)0) >= 0)
        {
            isBinary = true;
            return null;
        }

        try
        {
            isBinary = false;
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            isBinary = true;
            return null;
        }
    }

    private static async Task<TextGitResult> RunTextGitAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        ReadOnlyMemory<byte>? standardInput = null)
    {
        var binary = await RunBinaryGitAsync(
            workingDirectory,
            arguments,
            cancellationToken,
            standardInput: standardInput);
        return new TextGitResult(
            binary.ExitCode,
            Encoding.UTF8.GetString(binary.Bytes),
            binary.StdErr,
            binary.TimedOut);
    }

    internal static async Task<(bool TimedOut, byte[] Bytes)> RunBinaryGitForTestAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var result = await RunBinaryGitAsync(
            workingDirectory,
            arguments,
            cancellationToken,
            timeoutOverride: timeout);
        return (result.TimedOut, result.Bytes);
    }

    private static async Task<BinaryGitResult> RunBinaryGitAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        int? maximumOutputBytes = null,
        TimeSpan? timeoutOverride = null,
        ReadOnlyMemory<byte>? standardInput = null)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = standardInput.HasValue,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardErrorEncoding = Encoding.UTF8
            }
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        if (standardInput.HasValue)
        {
            if (!standardInput.Value.IsEmpty)
                await process.StandardInput.BaseStream.WriteAsync(standardInput.Value, cancellationToken);
            process.StandardInput.Close();
        }
        await using var output = new MemoryStream();
        var copyTask = CopyStandardOutputAsync(
            process.StandardOutput.BaseStream,
            output,
            maximumOutputBytes,
            cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeoutOverride ?? GitTimeout);
        var timedOut = false;
        var outputLimitExceeded = false;
        try
        {
            var waitTask = process.WaitForExitAsync(timeoutCts.Token);
            var completed = await Task.WhenAny(waitTask, copyTask);
            if (completed == copyTask)
            {
                outputLimitExceeded = await copyTask;
                if (outputLimitExceeded)
                {
                    TryKill(process);
                }
            }

            await waitTask;
            outputLimitExceeded |= await copyTask;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            timedOut = true;
            TryKill(process);
            await ObserveCopyTaskAsync(copyTask);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            await ObserveCopyTaskAsync(copyTask);
            throw;
        }

        var stdErr = await errorTask;
        return new BinaryGitResult(
            process.HasExited ? process.ExitCode : -1,
            output.ToArray(),
            stdErr,
            timedOut,
            outputLimitExceeded);
    }

    private static async Task<bool> CopyStandardOutputAsync(
        Stream source,
        Stream destination,
        int? maximumOutputBytes,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[81_920];
        var written = 0L;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                return false;
            }

            if (maximumOutputBytes is int limit && written + read > limit)
            {
                var allowed = Math.Max(0, limit - written);
                if (allowed > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, (int)allowed), cancellationToken);
                }

                return true;
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            written += read;
        }
    }

    private static async Task ObserveCopyTaskAsync(Task<bool> copyTask)
    {
        try
        {
            await copyTask;
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException)
        {
            // The process was killed for timeout/cancellation; its stdout pipe may close abruptly.
        }
    }

    private static void EnsureSucceeded(TextGitResult result, string operation, string directory)
    {
        if (result.TimedOut)
        {
            throw new TimeoutException($"Git timed out while trying to {operation} in {directory}.");
        }

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Git failed to {operation} in {directory}: {result.StdErr.Trim()}");
        }
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
            // Preserve the original cancellation or timeout.
        }
    }

    private static IEnumerable<string> SplitNull(string value) =>
        value.Split('\0', StringSplitOptions.RemoveEmptyEntries);

    private static bool IsAgentFile(string relativePath)
    {
        var name = Path.GetFileName(relativePath);
        return name.Equals("AGENT.md", StringComparison.OrdinalIgnoreCase)
            || name.Equals("AGENTS.md", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string path)
    {
        var normalized = path.Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        return normalized;
    }

    private static string ToFullPath(string root, string relativePath) =>
        Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static bool IsObjectId(string? value) =>
        value is { Length: 40 or 64 } && value.All(Uri.IsHexDigit);

    private sealed record StagedStatusEntry(
        string RelativePath,
        GitStagedChangeKind Kind,
        string? PreviousRelativePath);

    private sealed record GitIndexEntry(string RelativePath, string Mode);

    private sealed record GitTreeEntry(
        string RelativePath,
        string Mode,
        string Type,
        string ObjectId,
        long? Size);

    private sealed record BlobTextResult(string? Content, bool IsBinary)
    {
        public static BlobTextResult Binary { get; } = new(null, IsBinary: true);
    }

    private sealed class SnapshotMemoryBudget(long maximumBytes)
    {
        private long _reservedBytes;

        public int SkippedFileCount { get; private set; }

        public bool TryReserve(long bytes)
        {
            if (bytes > maximumBytes - _reservedBytes)
            {
                SkippedFileCount++;
                return false;
            }

            _reservedBytes += bytes;
            return true;
        }

        public void Release(long bytes) => _reservedBytes -= bytes;
    }

    private sealed record TextGitResult(int ExitCode, string StdOut, string StdErr, bool TimedOut);
    private sealed record BinaryGitResult(
        int ExitCode,
        byte[] Bytes,
        string StdErr,
        bool TimedOut,
        bool OutputLimitExceeded);
}
