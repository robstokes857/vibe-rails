using System.Globalization;
using System.Text.RegularExpressions;
using VibeRails.Services.Git;

namespace VibeRails.Services.Board;

public sealed record BoardCommitInfo(string Sha, string Author, string Message, DateTime CommittedUtc);

public sealed record BoardCommitDiffFile(string FileName, string Language, string OriginalContent, string ModifiedContent);

public sealed record BoardCommitDiff(IReadOnlyList<BoardCommitDiffFile> Files, int TotalChanges);

/// <summary>Captures commits from the caller's checkout for the card's saved Commits rail.</summary>
public interface IBoardCommitService
{
    /// <summary>Validates <paramref name="sha"/> against the repo and returns its metadata, or throws <see cref="BoardValidationException"/>.</summary>
    Task<BoardCommitInfo> DescribeAsync(string projectPath, string sha, CancellationToken cancellationToken = default);

    /// <summary>Per-file before/after contents in the shape the Monaco diff viewer consumes.</summary>
    Task<BoardCommitDiff> GetDiffAsync(string projectPath, string sha, CancellationToken cancellationToken = default);
}

public sealed partial class BoardCommitService : IBoardCommitService
{
    public const int MaxFiles = 60;
    public const int MaxFileChars = 400_000;
    private const int MaxListingChars = 1_000_000;
    // ASCII unit separator: git's %x1f, a byte that never appears in an author name or subject.
    private const char Unit = '\u001f';

    public async Task<BoardCommitInfo> DescribeAsync(string projectPath, string sha, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeSha(sha);
        var result = await GitCli.RunAsync(projectPath,
            ["show", "-s", "--no-color", $"--format=%H{Unit}%an{Unit}%cI{Unit}%s", normalized, "--"],
            cancellationToken);
        if (!result.Succeeded)
            throw new BoardValidationException($"Commit {normalized} was not found in this project's repository.");

        var parts = result.StdOut.Trim().Split(Unit);
        if (parts.Length < 4 || !ShaPattern().IsMatch(parts[0]))
            throw new BoardValidationException($"Commit {normalized} could not be read from git.");

        var committed = DateTime.TryParse(parts[2], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var when)
            ? when.ToUniversalTime()
            : DateTime.UtcNow;
        return new BoardCommitInfo(parts[0], parts[1].Trim(), parts[3].Trim(), committed);
    }

    public async Task<BoardCommitDiff> GetDiffAsync(string projectPath, string sha, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeSha(sha);
        var listing = await GitCli.RunAsync(projectPath,
            ["diff-tree", "--no-commit-id", "--raw", "--no-abbrev", "-z", "-r", "-M", "--root", "--diff-merges=first-parent", normalized, "--"],
            cancellationToken, maxOutputChars: MaxListingChars + 1);
        if (!listing.Succeeded)
            throw new BoardValidationException($"Commit {normalized} was not found in this project's repository.");
        if (listing.StdOut.Length > MaxListingChars)
            throw new BoardValidationException("The commit's file list is too large to capture.");
        if (listing.StdOut.Length > 0 && listing.StdOut[^1] != '\0')
            throw new BoardValidationException("Git returned an incomplete changed-file list. The commit was not linked.");

        // NUL delimiters preserve spaces, tabs and Unicode paths. Raw object ids pin both sides
        // to the commit rather than the mutable working tree, and also handle renames/gitlinks.
        var entries = listing.StdOut.Split('\0');
        var files = new List<BoardCommitDiffFile>();
        for (var index = 0; index < entries.Length - 1; index++)
        {
            if (files.Count >= MaxFiles)
                throw new BoardValidationException($"A commit snapshot can contain at most {MaxFiles} changed files.");
            var columns = entries[index].Split(' ');
            if (columns.Length != 5 || !columns[0].StartsWith(':')
                || !ShaPattern().IsMatch(columns[2]) || !ShaPattern().IsMatch(columns[3])
                || ++index >= entries.Length - 1)
                throw new BoardValidationException("Git returned an incomplete changed-file list. The commit was not linked.");
            var oldPath = entries[index];
            var newPath = oldPath;
            if (columns[4].StartsWith('R') || columns[4].StartsWith('C'))
            {
                if (++index >= entries.Length - 1)
                    throw new BoardValidationException("Git returned an incomplete rename. The commit was not linked.");
                newPath = entries[index];
            }

            var original = await ReadBlobAsync(projectPath, columns[0][1..], columns[2], oldPath, cancellationToken);
            var modified = await ReadBlobAsync(projectPath, columns[1], columns[3], newPath, cancellationToken);
            files.Add(new BoardCommitDiffFile(newPath, SandboxService.GetLanguageFromExtension(newPath), original, modified));
        }

        return new BoardCommitDiff(files, files.Count);
    }

    private static async Task<string> ReadBlobAsync(string projectPath, string mode, string objectId, string path, CancellationToken cancellationToken)
    {
        if (mode == "000000")
            return string.Empty; // This side of an added/deleted file does not exist.
        if (mode == "160000")
            return $"(submodule commit {objectId})";
        // Keep one extra character to detect truncation without ever buffering the full blob.
        var result = await GitCli.RunAsync(projectPath, ["cat-file", "blob", objectId], cancellationToken,
            maxOutputChars: MaxFileChars + 1);
        if (!result.Succeeded)
            throw new BoardValidationException($"Could not capture '{path}' from git. The commit was not linked.");
        var content = result.StdOut;
        if (content.Contains('\0'))
            return "(binary file)";
        return content.Length > MaxFileChars ? content[..MaxFileChars] + "\n… (truncated)" : content;
    }

    /// <summary>Only a hex sha reaches git — the value is an argument, never a shell string, but a ref like <c>HEAD~1</c> is still refused.</summary>
    public static string NormalizeSha(string? sha)
    {
        var trimmed = sha?.Trim() ?? string.Empty;
        if (!ShaPattern().IsMatch(trimmed))
            throw new BoardValidationException("That does not look like a commit sha.");
        return trimmed.ToLowerInvariant();
    }

    [GeneratedRegex("^[0-9a-fA-F]{7,40}$")]
    private static partial Regex ShaPattern();
}
