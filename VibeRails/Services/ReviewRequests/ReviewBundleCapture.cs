using System.Text;
using System.Text.Json;
using VibeRails.Services.GitPreflight;

namespace VibeRails.Services.ReviewRequests;

/// <summary>
/// Reads one path of the frozen base tree: binary (or unreadable) with no content, oversized with
/// no content, or the text.
/// </summary>
public delegate Task<(bool IsBinary, bool Oversized, string? Content)> ReviewBaseTextReader(string relativePath, CancellationToken cancellationToken);

/// <summary>
/// Reads one path of the frozen staged tree as its exact bytes, or null when it is missing, not a
/// blob or over the per-file byte bound (VIBE-211).
/// </summary>
public delegate Task<byte[]?> ReviewStagedBytesReader(string relativePath, CancellationToken cancellationToken);

/// <summary>
/// Turns a staged snapshot into the bundle a reviewer reads. Content is bounded per file and per
/// bundle; a file over either bound is listed with its change kind and line count but no text, and
/// says why, so the reviewer knows what they are not seeing instead of the bundle quietly shrinking.
/// A file listed without text still carries its exact staged bytes (<see cref="ReviewBundleFile.AfterBytes"/>,
/// base64) within <see cref="MaxFileBytes"/> and <see cref="MaxBundleBytes"/>, and every file its
/// modes, so the reviewer's machine can rebuild the tree (design decision 7) and the request can be
/// approved; a file whose bytes do not fit stays listed without content and the request is not approvable.
/// </summary>
/// <remarks>
/// <para>
/// The hook's snapshot carries no baseline for a deleted file and flags it binary (nothing is in the
/// index to read). A reviewer needs to see what was removed, so deleted paths are read from the
/// frozen base tree through <see cref="ReviewBaseTextReader"/>, under the same bounds.
/// </para>
/// <para>
/// Three budgets, so the hosted 24 MiB body bound is honest by construction and not by estimate:
/// text in UTF-8 bytes (<see cref="MaxBundleContentBytes"/>, what the host validates), carried bytes
/// raw (<see cref="MaxBundleBytes"/>, likewise), and the serialized size of every carried field as the
/// desktop's serializer escapes it (<see cref="MaxBundleWireBytes"/>: the default encoder writes a
/// non-ASCII or HTML-sensitive character, and the <c>+</c> of base64, as six bytes). A file that fits
/// the first two but not the third is listed as <c>bundle-limit</c>, like one past the text budget.
/// </para>
/// </remarks>
public static class ReviewBundleCapture
{
    public const int SchemaVersion = 1;

    /// <summary>Base plus staged text of one file, in UTF-8 bytes.</summary>
    public const int MaxFileContentBytes = 256 * 1024;

    /// <summary>All file text in one bundle, in UTF-8 bytes. Files are visited in path order.</summary>
    public const int MaxBundleContentBytes = 4 * 1024 * 1024;

    /// <summary>The exact staged bytes of one file listed without text, raw (VIBE-211).</summary>
    public const int MaxFileBytes = 4 * 1024 * 1024;

    /// <summary>All carried bytes in one bundle, raw.</summary>
    public const int MaxBundleBytes = 8 * 1024 * 1024;

    /// <summary>
    /// The serialized size of every carried text and byte field together, escaped as the desktop's
    /// serializer escapes them. Leaves 4 MiB of the hosted 24 MiB body bound for paths, sessions,
    /// reviewers, the note and the envelope.
    /// </summary>
    public const int MaxBundleWireBytes = 20 * 1024 * 1024;

    /// <summary>
    /// Reads deleted paths' base text with the snapshot provider, from the snapshot's own base tree.
    /// A blob over the per-file bound is reported oversized without being read.
    /// </summary>
    public static ReviewBaseTextReader BaseTextReader(GitStagedSnapshot snapshot) =>
        (path, ct) => GitStagedSnapshotProvider.TryReadTreeTextAsync(
            snapshot.RepositoryPath, snapshot.StagedIdentity?.BaseTree ?? string.Empty, path, MaxFileContentBytes, ct);

    /// <summary>Reads a staged path's exact bytes from the snapshot's own frozen staged tree, under <see cref="MaxFileBytes"/>.</summary>
    public static ReviewStagedBytesReader StagedBytesReader(GitStagedSnapshot snapshot) =>
        (path, ct) => GitStagedSnapshotProvider.TryReadTreeBytesAsync(
            snapshot.RepositoryPath, snapshot.StagedIdentity?.StagedTree ?? string.Empty, path, MaxFileBytes, ct);

    public static async Task<ReviewBundle> BuildAsync(
        GitStagedSnapshot snapshot,
        ReviewBaseTextReader? readBaseText,
        ReviewStagedBytesReader? readStagedBytes,
        IReadOnlyList<ReviewBundleSession> sessions,
        IReadOnlyList<string> reviewers,
        string? cardKey,
        string? note,
        DateTime createdUtc,
        CancellationToken cancellationToken)
    {
        var identity = snapshot.StagedIdentity
            ?? throw new InvalidOperationException("The snapshot carries no staged tree identity.");
        var textBudget = MaxBundleContentBytes;
        var bytesBudget = MaxBundleBytes;
        var wireBudget = (long)MaxBundleWireBytes;
        var files = new List<ReviewBundleFile>(snapshot.Files.Count);
        foreach (var file in snapshot.Files.OrderBy(file => file.RelativePath, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var deleted = file.ChangeKind == GitStagedChangeKind.Deleted;
            var binary = file.IsBinary;
            var oversized = false;
            var baseText = file.PreviousContent;
            var stagedText = file.Content;
            if (deleted && readBaseText is not null)
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
                else if (size > textBudget)
                    omitted = ReviewBundleOmission.BundleLimit;
                else
                {
                    var wire = WireSize(stagedText) + WireSize(baseText);
                    if (wire > wireBudget)
                        omitted = ReviewBundleOmission.BundleLimit;
                    else
                    {
                        before = baseText;
                        after = stagedText;
                        textBudget -= size;
                        wireBudget -= wire;
                    }
                }
            }

            // A staged side the reviewer cannot read as text still has to reach the rebuild. A gitlink
            // names a commit, not readable bytes, and stays unapprovable.
            var mode = deleted ? null : file.StagedMode;
            var previousMode = file.ChangeKind == GitStagedChangeKind.Added ? null : file.PreviousMode;
            string? afterBytes = null;
            if (omitted is not null && !deleted && readStagedBytes is not null && mode != ReviewFileMode.GitLink)
            {
                var bytes = await readStagedBytes(file.RelativePath, cancellationToken);
                if (bytes is not null && bytes.Length <= MaxFileBytes && bytes.Length <= bytesBudget)
                {
                    var encoded = Convert.ToBase64String(bytes);
                    var wire = WireSize(encoded);
                    if (wire <= wireBudget)
                    {
                        afterBytes = encoded;
                        bytesBudget -= bytes.Length;
                        wireBudget -= wire;
                    }
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
                omitted,
                afterBytes,
                mode,
                previousMode));
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

    /// <summary>
    /// Decodes a file's carried bytes when they are well-formed base64 of at most <paramref name="maxBytes"/>.
    /// The length is checked before anything is allocated, so an oversized field costs nothing to refuse.
    /// </summary>
    public static bool TryDecodeBytes(string? base64, int maxBytes, out byte[] bytes)
    {
        bytes = [];
        if (base64 is null || base64.Length == 0 || base64.Length % 4 != 0 || base64.Length / 4 * 3 > (long)maxBytes + 2)
            return false;
        var buffer = new byte[base64.Length / 4 * 3];
        if (!Convert.TryFromBase64String(base64, buffer, out var written) || written > maxBytes)
            return false;
        bytes = written == buffer.Length ? buffer : buffer[..written];
        return true;
    }

    /// <summary>The raw length of a base64 field, for counts shown before and after a send; 0 for none.</summary>
    public static long DecodedLength(string? base64)
    {
        if (base64 is not { Length: > 0 }) return 0;
        var padding = base64.EndsWith("==", StringComparison.Ordinal) ? 2 : base64.EndsWith('=') ? 1 : 0;
        return (long)base64.Length / 4 * 3 - padding;
    }

    private static int Utf8Length(string? text) => text is null ? 0 : Encoding.UTF8.GetByteCount(text);

    /// <summary>The bytes a JSON string field costs on the wire with the desktop's serializer: escaped, with its quotes.</summary>
    internal static long WireSize(string? text) => text is null ? 0 : JsonEncodedText.Encode(text).EncodedUtf8Bytes.Length + 2L;

    private static string RepositoryName(string repositoryPath)
    {
        var trimmed = repositoryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? trimmed : name;
    }
}
