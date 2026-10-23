using System.Text;
using VibeRails.Services.GitPreflight;

namespace VibeRails.Services.ReviewRequests;

/// <summary>
/// Reads one path of the frozen base tree: binary (or unreadable) with no content, oversized with
/// no content, or the text.
/// </summary>
public delegate Task<(bool IsBinary, bool Oversized, string? Content)> ReviewBaseTextReader(string relativePath, CancellationToken cancellationToken);

/// <summary>
/// Turns a staged snapshot into the bundle a reviewer reads. Content is bounded per file and per
/// bundle; a file over either bound is listed with its change kind and line count but no text, and
/// says why, so the reviewer knows what they are not seeing instead of the bundle quietly shrinking.
/// </summary>
/// <remarks>
/// The hook's snapshot carries no baseline for a deleted file and flags it binary (nothing is in the
/// index to read). A reviewer needs to see what was removed, so deleted paths are read from the
/// frozen base tree through <see cref="ReviewBaseTextReader"/>, under the same bounds.
/// </remarks>
public static class ReviewBundleCapture
{
    public const int SchemaVersion = 1;

    /// <summary>Base plus staged text of one file, in UTF-8 bytes.</summary>
    public const int MaxFileContentBytes = 256 * 1024;

    /// <summary>All file text in one bundle, in UTF-8 bytes. Files are visited in path order.</summary>
    public const int MaxBundleContentBytes = 4 * 1024 * 1024;

    /// <summary>
    /// Reads deleted paths' base text with the snapshot provider, from the snapshot's own base tree.
    /// A blob over the per-file bound is reported oversized without being read.
    /// </summary>
    public static ReviewBaseTextReader BaseTextReader(GitStagedSnapshot snapshot) =>
        (path, ct) => GitStagedSnapshotProvider.TryReadTreeTextAsync(
            snapshot.RepositoryPath, snapshot.StagedIdentity?.BaseTree ?? string.Empty, path, MaxFileContentBytes, ct);

    public static async Task<ReviewBundle> BuildAsync(
        GitStagedSnapshot snapshot,
        ReviewBaseTextReader? readBaseText,
        IReadOnlyList<ReviewBundleSession> sessions,
        IReadOnlyList<string> reviewers,
        string? cardKey,
        string? note,
        DateTime createdUtc,
        CancellationToken cancellationToken)
    {
        var identity = snapshot.StagedIdentity
            ?? throw new InvalidOperationException("The snapshot carries no staged tree identity.");
        var budget = MaxBundleContentBytes;
        var files = new List<ReviewBundleFile>(snapshot.Files.Count);
        foreach (var file in snapshot.Files.OrderBy(file => file.RelativePath, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var binary = file.IsBinary;
            var oversized = false;
            var baseText = file.PreviousContent;
            var stagedText = file.Content;
            if (file.ChangeKind == GitStagedChangeKind.Deleted && readBaseText is not null)
            {
                // The index has nothing at this path, so the snapshot could only say "binary";
                // what the reviewer wants is the text that is going away.
                var (baseIsBinary, baseIsOversized, content) = await readBaseText(file.PreviousRelativePath ?? file.RelativePath, cancellationToken);
                binary = baseIsBinary;
                oversized = baseIsOversized;
                baseText = baseIsBinary || baseIsOversized ? null : content;
                stagedText = null;
            }

            string? before = null, after = null, omitted = null;
            if (binary)
            {
                omitted = ReviewBundleOmission.Binary;
            }
            else
            {
                var size = Utf8Length(stagedText) + Utf8Length(baseText);
                if (oversized || size > MaxFileContentBytes)
                    omitted = ReviewBundleOmission.Oversized;
                else if (size > budget)
                    omitted = ReviewBundleOmission.BundleLimit;
                else
                {
                    before = baseText;
                    after = stagedText;
                    budget -= size;
                }
            }
            files.Add(new ReviewBundleFile(
                file.RelativePath,
                Describe(file.ChangeKind),
                binary,
                file.PreviousRelativePath,
                file.ChangedLineCount,
                before,
                after,
                omitted));
        }

        return new ReviewBundle(
            SchemaVersion,
            RepositoryName(snapshot.RepositoryPath),
            ReviewScope.Staged,
            identity.StagedTree,
            identity.BaseTree,
            identity.BaseCommit,
            files,
            sessions,
            reviewers,
            cardKey,
            note,
            createdUtc);
    }

    internal static string Describe(GitStagedChangeKind kind) => kind switch
    {
        GitStagedChangeKind.Added => "added",
        GitStagedChangeKind.Modified => "modified",
        GitStagedChangeKind.Deleted => "deleted",
        GitStagedChangeKind.Renamed => "renamed",
        GitStagedChangeKind.Copied => "copied",
        GitStagedChangeKind.Unmerged => "unmerged",
        _ => "unknown"
    };

    private static int Utf8Length(string? text) => text is null ? 0 : Encoding.UTF8.GetByteCount(text);

    private static string RepositoryName(string repositoryPath)
    {
        var trimmed = repositoryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? trimmed : name;
    }
}
