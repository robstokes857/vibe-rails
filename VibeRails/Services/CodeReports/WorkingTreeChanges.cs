using System.Text;
using VibeRails.DTOs;
using VibeRails.Services.Git;
using VibeRails.Services.GitPreflight;

namespace VibeRails.Services.CodeReports;

/// <summary>
/// Lists the working tree's changes against HEAD for the Code quality card and reads one changed
/// file's before/after text for the shared diff viewer. Read-only: git runs with argv only, every
/// path is repository-relative, and the working-tree path guard refuses links and escapes.
/// </summary>
public sealed class WorkingTreeChanges
{
    /// <summary>Entries beyond this are counted but not listed; the response says so.</summary>
    public const int MaxFiles = 2000;
    /// <summary>Characters retained per side of a diff; longer text is cut and flagged.</summary>
    public const int MaxFileChars = 1_000_000;
    /// <summary>Bytes read from one side of a diff before it is reported as too large to show.</summary>
    public const long MaxFileBytes = 5 * 1024 * 1024;
    private const int MaxListingChars = 4_000_000;
    // Untracked files have no numstat; their lines are counted unless the file is larger than this.
    private const long MaxCountBytes = 2 * 1024 * 1024;
    private static readonly TimeSpan ListTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Statuses, staging flags and line counts for every change git reports against HEAD.</summary>
    public async Task<WorkingTreeChangesResponse> ListAsync(string repositoryPath, CancellationToken cancellationToken)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryPath));
        var head = await GitCli.RunAsync(root, ["rev-parse", "--verify", "--quiet", "HEAD"], cancellationToken, ListTimeout, maxOutputChars: 128);
        if (head.TimedOut) throw Timeout("rev-parse");
        // An unborn branch has no HEAD: every file is new and there is nothing to count against.
        var headSha = head.Succeeded ? head.StdOut.Trim() : null;

        var status = await GitCli.RunAsync(root,
            ["status", "--porcelain=v1", "-z", "--untracked-files=all", "--ignore-submodules"],
            cancellationToken, ListTimeout, maxOutputChars: MaxListingChars + 1);
        if (status.TimedOut) throw Timeout("status");
        if (!status.Succeeded) throw new InvalidOperationException("Could not read the working tree's changes.");
        if (status.StdOut.Length > MaxListingChars) throw new InvalidOperationException("The working tree has too many changes to list.");

        var counts = headSha is null ? new Dictionary<string, LineCounts>(StringComparer.Ordinal) : await ReadNumstatAsync(root, cancellationToken);
        var guard = new GitStagedSnapshotProvider.WorkingTreePathGuard(root);
        var files = new List<WorkingTreeChangeFile>();
        int additions = 0, deletions = 0, total = 0;
        foreach (var entry in ParseStatus(status.StdOut))
        {
            if (!RepositoryCodeGraph.IsSafePath(entry.Path)) continue;
            total++;
            if (files.Count >= MaxFiles) continue;
            var count = counts.TryGetValue(entry.Path, out var known) ? known
                : entry.Status == "untracked" ? await CountLinesAsync(guard, Path.Combine(root, entry.Path), cancellationToken)
                : LineCounts.Unknown;
            additions += count.Added ?? 0;
            deletions += count.Deleted ?? 0;
            files.Add(new WorkingTreeChangeFile(entry.Path, entry.Status, entry.Staged, entry.Unstaged,
                count.Added, count.Deleted, count.Binary, entry.OriginalPath));
        }
        return new WorkingTreeChangesResponse(total, additions, deletions, total > files.Count, files, DateTime.UtcNow, headSha);
    }

    /// <summary>
    /// HEAD's text and the working tree's text for one path, or null when neither side exists.
    /// A staged rename or copy keeps its "before" text at <paramref name="originalPath"/> in HEAD,
    /// which the change list reports; without it the new path has no HEAD side and reads as added.
    /// Binary content is reported, not returned; oversized sides are cut and flagged.
    /// </summary>
    public async Task<WorkingTreeDiffResponse?> ReadDiffAsync(string repositoryPath, string relativePath,
        CancellationToken cancellationToken, string? originalPath = null)
    {
        if (!RepositoryCodeGraph.IsSafePath(relativePath))
            throw new InvalidOperationException("A repository-relative path is required.");
        if (originalPath is not null && !RepositoryCodeGraph.IsSafePath(originalPath))
            throw new InvalidOperationException("The original path must be repository-relative.");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryPath));
        var original = await ReadHeadAsync(root, originalPath ?? relativePath, cancellationToken);
        var modified = await ReadWorkingTreeAsync(root, relativePath, cancellationToken);
        if (!original.Exists && !modified.Exists) return null;
        var status = !original.Exists ? "added" : !modified.Exists ? "deleted" : originalPath is not null ? "renamed" : "modified";
        var binary = original.Binary || modified.Binary;
        return new WorkingTreeDiffResponse(relativePath, SandboxService.GetLanguageFromExtension(relativePath), status,
            binary ? string.Empty : original.Text, binary ? string.Empty : modified.Text, binary,
            original.Truncated || modified.Truncated);
    }

    internal readonly record struct LineCounts(int? Added, int? Deleted, bool Binary)
    {
        public static LineCounts Unknown { get; } = new(null, null, false);
    }

    internal sealed record StatusEntry(string Path, string Status, bool Staged, bool Unstaged, string? OriginalPath);

    private readonly record struct Side(bool Exists, string Text, bool Binary, bool Truncated)
    {
        public static Side Missing { get; } = new(false, string.Empty, false, false);
    }

    /// <summary>Parses <c>git status --porcelain=v1 -z</c>: XY, a space, the path; renames add the original path as its own field.</summary>
    internal static List<StatusEntry> ParseStatus(string output)
    {
        var fields = output.Split('\0');
        var entries = new List<StatusEntry>();
        for (var index = 0; index < fields.Length; index++)
        {
            var field = fields[index];
            if (field.Length < 4 || field[2] != ' ') continue;
            char x = field[0], y = field[1];
            var path = field[3..].Replace('\\', '/');
            string? original = null;
            if (x is 'R' or 'C' && index + 1 < fields.Length) original = fields[++index].Replace('\\', '/');
            var untracked = x == '?' && y == '?';
            var status = untracked ? "untracked"
                : x == 'U' || y == 'U' || (x == 'A' && y == 'A') || (x == 'D' && y == 'D') ? "conflicted"
                : x == 'D' || y == 'D' ? "deleted"
                : x == 'R' ? "renamed"
                : x == 'C' ? "copied"
                : x == 'A' ? "added"
                : x == 'T' || y == 'T' ? "typechange"
                : "modified";
            entries.Add(new StatusEntry(path, status, Staged: !untracked && x != ' ', Unstaged: untracked || y != ' ', original));
        }
        return entries;
    }

    /// <summary>Parses <c>git diff --numstat -z</c>; a rename's empty path is followed by the old and new paths.</summary>
    internal static Dictionary<string, LineCounts> ParseNumstat(string output)
    {
        var counts = new Dictionary<string, LineCounts>(StringComparer.Ordinal);
        var fields = output.Split('\0');
        for (var index = 0; index < fields.Length; index++)
        {
            var parts = fields[index].Split('\t');
            if (parts.Length < 3) continue;
            var binary = parts[0] == "-" || parts[1] == "-";
            int? added = !binary && int.TryParse(parts[0], out var a) ? a : null;
            int? deleted = !binary && int.TryParse(parts[1], out var d) ? d : null;
            var path = parts[2];
            if (path.Length == 0)
            {
                if (index + 2 >= fields.Length) break;
                index += 2;
                path = fields[index];
            }
            counts[path.Replace('\\', '/')] = new LineCounts(added, deleted, binary);
        }
        return counts;
    }

    private static async Task<Dictionary<string, LineCounts>> ReadNumstatAsync(string root, CancellationToken cancellationToken)
    {
        var result = await GitCli.RunAsync(root, ["diff", "--numstat", "-z", "-M", "HEAD", "--"],
            cancellationToken, ListTimeout, maxOutputChars: MaxListingChars + 1);
        if (result.TimedOut) throw Timeout("diff");
        // Counts decorate the list; a diff that cannot be read leaves the statuses standing.
        if (!result.Succeeded || result.StdOut.Length > MaxListingChars) return new Dictionary<string, LineCounts>(StringComparer.Ordinal);
        return ParseNumstat(result.StdOut);
    }

    private static async Task<LineCounts> CountLinesAsync(GitStagedSnapshotProvider.WorkingTreePathGuard guard, string fullPath, CancellationToken cancellationToken)
    {
        if (!guard.IsReadableRegularFile(fullPath) || new FileInfo(fullPath).Length > MaxCountBytes) return LineCounts.Unknown;
        byte[] bytes;
        try { bytes = await File.ReadAllBytesAsync(fullPath, cancellationToken); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return LineCounts.Unknown; }
        // Git's own heuristic: a NUL in the first 8000 bytes makes a file binary.
        if (bytes.AsSpan(0, Math.Min(bytes.Length, 8000)).IndexOf((byte)0) >= 0) return new LineCounts(null, null, true);
        var lines = 0;
        foreach (var value in bytes) if (value == (byte)'\n') lines++;
        if (bytes.Length > 0 && bytes[^1] != (byte)'\n') lines++;
        return new LineCounts(lines, 0, false);
    }

    private static async Task<Side> ReadHeadAsync(string root, string relativePath, CancellationToken cancellationToken)
    {
        // "HEAD:./path" is one argument: no option parsing, no globbing, and the leading ./ pins a relative path.
        var spec = "HEAD:./" + relativePath;
        var size = await GitCli.RunAsync(root, ["cat-file", "-s", spec], cancellationToken, ListTimeout, maxOutputChars: 64);
        if (size.TimedOut) throw Timeout("cat-file");
        if (!size.Succeeded || !long.TryParse(size.StdOut.Trim(), out var bytes)) return Side.Missing;
        if (bytes > MaxFileBytes) return new Side(true, string.Empty, false, true);
        var blob = await GitCli.RunAsync(root, ["cat-file", "blob", spec], cancellationToken, ListTimeout, maxOutputChars: MaxFileChars + 1);
        if (blob.TimedOut) throw Timeout("cat-file");
        if (!blob.Succeeded) return Side.Missing;
        if (blob.StdOut.Contains('\0')) return new Side(true, string.Empty, true, false);
        return blob.StdOut.Length > MaxFileChars
            ? new Side(true, blob.StdOut[..MaxFileChars], false, true)
            : new Side(true, blob.StdOut, false, false);
    }

    private static async Task<Side> ReadWorkingTreeAsync(string root, string relativePath, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
        var guard = new GitStagedSnapshotProvider.WorkingTreePathGuard(root);
        if (!guard.IsReadableRegularFile(fullPath)) return Side.Missing;
        if (new FileInfo(fullPath).Length > MaxFileBytes) return new Side(true, string.Empty, false, true);
        byte[] bytes;
        try { bytes = await File.ReadAllBytesAsync(fullPath, cancellationToken); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Side.Missing; }
        var text = GitStagedSnapshotProvider.DecodeText(bytes, out var binary);
        if (binary || text is null) return new Side(true, string.Empty, true, false);
        return text.Length > MaxFileChars ? new Side(true, text[..MaxFileChars], false, true) : new Side(true, text, false, false);
    }

    private static InvalidOperationException Timeout(string command) => new(
        $"Git did not answer `{command}` within {ListTimeout.TotalSeconds:0} seconds. Retry, and check for a stuck git process if it persists.");
}
