using System.Diagnostics;
using System.Text;

namespace VibeRails.Services.GitPreflight;

public sealed class GitStagedSnapshotProvider : IGitStagedSnapshotProvider
{
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(15);
    private const int MaximumTextFileBytes = 5 * 1024 * 1024;
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

            changedLines.TryGetValue(entry.RelativePath, out var lineCount);
            files.Add(new GitStagedFileSnapshot(
                entry.RelativePath,
                ToFullPath(repositoryPath, entry.RelativePath),
                entry.Kind,
                existsInIndex,
                isBinary,
                lineCount,
                content,
                entry.PreviousRelativePath));
        }

        var agentFiles = await ReadAgentFilesAsync(repositoryPath, cancellationToken);
        return new GitStagedSnapshot(repositoryPath, files, agentFiles);
    }

    private static async Task<IReadOnlyList<GitIndexTextFile>> ReadAgentFilesAsync(
        string repositoryPath,
        CancellationToken cancellationToken)
    {
        var indexResult = await RunTextGitAsync(
            repositoryPath,
            ["ls-files", "-z"],
            cancellationToken);
        EnsureSucceeded(indexResult, "read the Git index", repositoryPath);

        var paths = SplitNull(indexResult.StdOut)
            .Select(NormalizePath)
            .Where(IsAgentFile)
            .Distinct(PathComparer)
            .OrderBy(path => path, StringComparer.Ordinal)
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

        var counts = new Dictionary<string, int?>(PathComparer);
        var records = SplitNull(result.StdOut).ToList();
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
