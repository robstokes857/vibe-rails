using System.Diagnostics;
using System.Text;
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
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

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

        var statusResult = await RunTextGitAsync(
            repositoryPath,
            ["--no-pager", "diff", "--cached", "--name-status", "-z", "--find-renames"],
            cancellationToken);
        EnsureSucceeded(statusResult, "read staged file status", repositoryPath);

        var stagedEntries = ParseNameStatus(statusResult.StdOut);
        var changedLines = await ReadChangedLineCountsAsync(repositoryPath, cancellationToken);
        var files = new List<GitStagedFileSnapshot>(stagedEntries.Count);

        foreach (var entry in stagedEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var existsInIndex = entry.Kind != GitStagedChangeKind.Deleted;
            string? content = null;
            var isBinary = false;

            if (existsInIndex)
            {
                var blob = await ReadIndexBlobAsync(repositoryPath, entry.RelativePath, cancellationToken);
                content = DecodeText(blob, out isBinary);
            }

            // The committed version of the file, so consumers can tell newly introduced
            // problems apart from ones the file already had. Modified/renamed files only;
            // added and copied paths had no code at this location before.
            string? previousContent = null;
            if (!isBinary && entry.Kind is GitStagedChangeKind.Modified or GitStagedChangeKind.Renamed)
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

        var trackedFiles = await ReadTrackedFilesAsync(repositoryPath, cancellationToken);
        var agentFiles = await ReadAgentFilesAsync(repositoryPath, trackedFiles, cancellationToken);
        return new GitStagedSnapshot(repositoryPath, files, agentFiles, trackedFiles);
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

        var trackedFiles = await ReadTrackedFilesAsync(repositoryPath, cancellationToken);
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

        foreach (var path in SplitNull(untrackedResult.StdOut)
            .Select(NormalizePath)
            .Where(path => path.Length > 0))
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
            var existsInWorkingTree = entry.Kind != GitStagedChangeKind.Deleted
                && pathGuard.IsReadableRegularFile(fullPath);
            string? content = null;
            var isBinary = false;
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

        var agentFiles = await ReadAgentFilesAsync(repositoryPath, trackedFiles, cancellationToken);
        return new GitStagedSnapshot(repositoryPath, files, agentFiles, trackedFiles);
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
    /// </summary>
    private sealed class WorkingTreePathGuard(string repositoryPath)
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

    private static async Task<IReadOnlyList<string>> ReadTrackedFilesAsync(
        string repositoryPath,
        CancellationToken cancellationToken)
    {
        var indexResult = await RunTextGitAsync(
            repositoryPath,
            ["ls-files", "-z"],
            cancellationToken);
        EnsureSucceeded(indexResult, "read the Git index", repositoryPath);

        return SplitNull(indexResult.StdOut)
            .Select(NormalizePath)
            .Distinct(PathComparer)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();
    }

    private static async Task<IReadOnlyList<GitIndexTextFile>> ReadAgentFilesAsync(
        string repositoryPath,
        IReadOnlyList<string> trackedFiles,
        CancellationToken cancellationToken)
    {
        var paths = trackedFiles
            .Where(IsAgentFile)
            .ToList();
        var results = new List<GitIndexTextFile>(paths.Count);

        foreach (var path in paths)
        {
            var bytes = await ReadIndexBlobAsync(repositoryPath, path, cancellationToken);
            var content = DecodeText(bytes, out var binary);
            if (!binary && content != null)
            {
                results.Add(new GitIndexTextFile(path, content));
            }
        }

        return results;
    }

    private static async Task<Dictionary<string, int?>> ReadChangedLineCountsAsync(
        string repositoryPath,
        CancellationToken cancellationToken)
    {
        var result = await RunTextGitAsync(
            repositoryPath,
            ["--no-pager", "diff", "--cached", "--numstat", "-z"],
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

    private static async Task<byte[]> ReadIndexBlobAsync(
        string repositoryPath,
        string relativePath,
        CancellationToken cancellationToken)
    {
        var result = await RunBinaryGitAsync(
            repositoryPath,
            ["--no-pager", "show", "--no-textconv", $":./{relativePath}"],
            cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Git could not read staged content for '{relativePath}': {result.StdErr.Trim()}");
        }

        return result.Bytes;
    }

    /// <summary>
    /// Reads the committed (HEAD) version of a path. Returns null instead of throwing:
    /// an unborn branch or a path new to this commit simply has no baseline.
    /// </summary>
    private static async Task<byte[]?> TryReadHeadBlobAsync(
        string repositoryPath,
        string relativePath,
        CancellationToken cancellationToken)
    {
        var result = await RunBinaryGitAsync(
            repositoryPath,
            ["--no-pager", "show", "--no-textconv", $"HEAD:./{relativePath}"],
            cancellationToken);
        return result.ExitCode == 0 && !result.TimedOut ? result.Bytes : null;
    }

    private static string? DecodeText(byte[] bytes, out bool isBinary)
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
        CancellationToken cancellationToken)
    {
        var binary = await RunBinaryGitAsync(workingDirectory, arguments, cancellationToken);
        return new TextGitResult(
            binary.ExitCode,
            Encoding.UTF8.GetString(binary.Bytes),
            binary.StdErr,
            binary.TimedOut);
    }

    private static async Task<BinaryGitResult> RunBinaryGitAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
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
        await using var output = new MemoryStream();
        var copyTask = process.StandardOutput.BaseStream.CopyToAsync(output, cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(GitTimeout);
        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
            await copyTask;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            timedOut = true;
            TryKill(process);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        var stdErr = await errorTask;
        return new BinaryGitResult(
            process.HasExited ? process.ExitCode : -1,
            output.ToArray(),
            stdErr,
            timedOut);
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

    private sealed record StagedStatusEntry(
        string RelativePath,
        GitStagedChangeKind Kind,
        string? PreviousRelativePath);

    private sealed record TextGitResult(int ExitCode, string StdOut, string StdErr, bool TimedOut);
    private sealed record BinaryGitResult(int ExitCode, byte[] Bytes, string StdErr, bool TimedOut);
}
