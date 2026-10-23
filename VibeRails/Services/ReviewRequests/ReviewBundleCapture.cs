using System.Text;
using VibeRails.Services.GitPreflight;

namespace VibeRails.Services.ReviewRequests;

/// <summary>
/// Turns a staged snapshot into the bundle a reviewer reads. Content is bounded per file and per
/// bundle; a file over either bound is listed with its change kind and line count but no text, and
/// says why, so the reviewer knows what they are not seeing instead of the bundle quietly shrinking.
/// </summary>
public static class ReviewBundleCapture
{
    public const int SchemaVersion = 1;

    /// <summary>Base plus staged text of one file, in UTF-8 bytes.</summary>
    public const int MaxFileContentBytes = 256 * 1024;

    /// <summary>All file text in one bundle, in UTF-8 bytes. Files are visited in path order.</summary>
    public const int MaxBundleContentBytes = 4 * 1024 * 1024;

    public static ReviewBundle FromSnapshot(
        GitStagedSnapshot snapshot,
        IReadOnlyList<ReviewBundleSession> sessions,
        IReadOnlyList<string> reviewers,
        string? cardKey,
        string? note,
        DateTime createdUtc)
    {
        var identity = snapshot.StagedIdentity
            ?? throw new InvalidOperationException("The snapshot carries no staged tree identity.");
        var budget = MaxBundleContentBytes;
        var files = new List<ReviewBundleFile>(snapshot.Files.Count);
        foreach (var file in snapshot.Files.OrderBy(file => file.RelativePath, StringComparer.Ordinal))
        {
            string? before = null, after = null, omitted = null;
            if (file.IsBinary)
            {
                omitted = ReviewBundleOmission.Binary;
            }
            else
            {
                var size = Utf8Length(file.Content) + Utf8Length(file.PreviousContent);
                if (size > MaxFileContentBytes)
                    omitted = ReviewBundleOmission.Oversized;
                else if (size > budget)
                    omitted = ReviewBundleOmission.BundleLimit;
                else
                {
                    before = file.PreviousContent;
                    after = file.Content;
                    budget -= size;
                }
            }
            files.Add(new ReviewBundleFile(
                file.RelativePath,
                Describe(file.ChangeKind),
                file.IsBinary,
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
