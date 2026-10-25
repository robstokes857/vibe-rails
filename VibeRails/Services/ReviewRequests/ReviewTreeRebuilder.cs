using System.Security.Cryptography;
using System.Text;
using VibeRails.Services.Git;
using VibeRails.Services.VCA;

namespace VibeRails.Services.ReviewRequests;

/// <summary>
/// The verdict of <see cref="ReviewTreeRebuilder.RebuildAsync"/>. <see cref="Matches"/> is the only
/// outcome under which a review request may be signed; every other <see cref="Code"/> explains why not,
/// in wording the popup prints as is.
/// </summary>
/// <param name="Code"><c>matches</c>, <c>missing_content</c>, <c>unsupported_change</c>, <c>too_many_files</c>,
/// <c>inconsistent_change</c>, <c>not_a_repository</c>, <c>base_commit_missing</c>, <c>base_tree_mismatch</c>,
/// <c>base_mismatch</c>, <c>base_text_missing</c>, <c>hidden_change</c>, <c>tree_mismatch</c>, <c>git_failed</c>.</param>
/// <param name="MissingContent">The paths listed without their content, for <c>missing_content</c>.</param>
public sealed record ReviewTreeRebuildResult(
    bool Matches,
    string Code,
    string Message,
    string? RebuiltTree = null,
    IReadOnlyList<string>? MissingContent = null);

/// <summary>
/// Proves that a review bundle's files make up the tree an approval would sign (VB-50 design decision 7).
/// The requester supplies both the tree id and the file text, so viberails.ai cannot bind one to the
/// other; this does, on the reviewer's machine, from the reviewer's own copy of the base commit: a
/// temporary index is loaded with the base tree, each listed file's staged content becomes an index
/// entry whose blob id is computed here from its exact UTF-8 bytes, deletions and renames are applied,
/// and <c>write-tree</c> must produce <c>treeId</c>. Nothing is written to the repository's working
/// tree or real index, and no blob is stored: the tree object alone is written, unreachable, and git
/// prunes it. Every git launch goes through <see cref="GitCli"/>.
/// </summary>
/// <remarks>
/// <para>
/// Two things are proven, because the reviewer read two things. The tree binds the staged text: an
/// unlisted or altered file changes <c>write-tree</c>. The base binds the diff the reviewer saw: each
/// transition has the canonical shape (<see cref="ShapeProblem"/>), each changed file's base text must
/// hash to the base commit's own blob at its source path, a file listed as added and the destination of
/// a rename or copy must be absent from the base, a deleted or changed file must exist there, and the
/// per-path transition the rebuilt tree makes relative to the base must equal the listed one
/// (<see cref="TransitionMismatches"/>).
/// Without those checks a request could show <c>Before = After</c> ("no text changes") over a real
/// change, point a file's base text at another file, or add a path that collides with a base directory
/// or file so that unlisted deletions ride along, and still collect a signature over the true tree
/// (Codex reviews of slice D, F1 and its variants).
/// </para>
/// <para>
/// A file listed without text carries its exact staged bytes instead (<see cref="ReviewBundleFile.AfterBytes"/>,
/// VIBE-211), hashed here like text; each file's staged mode places a new executable or a symlink as the
/// requester staged it, and a carried base mode must be the base commit's. What this cannot know, and
/// therefore refuses rather than guesses: a file listed without content and without bytes has nothing to
/// hash; a base commit the reviewer does not have cannot be read; a submodule entry names a commit this
/// repository may not have; a bundle from an older VibeRails carries no modes, so a new file is assumed to
/// be a plain file (<c>100644</c>) and a changed file keeps the mode its base entry had. A mismatch for any
/// reason is a refusal: the reviewer cannot tell a hidden change from a mode bit, and must not sign either.
/// </para>
/// </remarks>
public static class ReviewTreeRebuilder
{
    /// <summary>The hosted bundle bound, so the rebuild never takes more than one bundle's worth of work.</summary>
    public const int MaxFiles = 5000;

    /// <summary>Bounds every git call in one rebuild. A monorepo's <c>read-tree</c> can take a few seconds.</summary>
    public static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(60);

    private const string PlainFileMode = "100644";
    private const string EmptyTreeSha1 = "4b825dc642cb6eb9a060e54bf8d69288fbee4904";
    private const string EmptyTreeSha256 = "6ef19b41225c5369f1c104d45d8d85efa9b057b53b14b4b9b939dd74decc5321";

    /// <summary>Pathspecs per <c>ls-files</c> call: well under the Windows command-line limit with long paths.</summary>
    private const int PathsPerListing = 200;
    private const int CharsPerListing = 24_000;

    /// <summary>
    /// Files whose staged content is in the bundle neither as text nor as bytes. A deleted file needs
    /// none: it has no staged content. Carried bytes that are not readable base64 count as absent.
    /// </summary>
    public static IReadOnlyList<string> FilesWithoutContent(IReadOnlyList<ReviewBundleFile> files) =>
        files.Where(file => file.Change != "deleted" && !TryStagedBytes(file, out _)).Select(file => file.Path).ToList();

    /// <summary>
    /// The exact staged bytes of a file: its text as UTF-8 (capture decoded strict UTF-8, so the round trip
    /// is byte-exact, byte-order mark and CRLF included) or its carried bytes decoded. False for a file
    /// listed without either, or whose bytes are not base64 within the per-file bound.
    /// </summary>
    internal static bool TryStagedBytes(ReviewBundleFile file, out byte[] bytes)
    {
        if (file.After is not null)
        {
            bytes = Encoding.UTF8.GetBytes(file.After);
            return true;
        }
        return ReviewBundleCapture.TryDecodeBytes(file.AfterBytes, ReviewBundleCapture.MaxFileBytes, out bytes);
    }

    public static async Task<ReviewTreeRebuildResult> RebuildAsync(
        string repositoryPath,
        string treeId,
        string baseTree,
        string? baseCommit,
        IReadOnlyList<ReviewBundleFile> files,
        CancellationToken cancellationToken)
    {
        if (!CommitSignatureFormat.IsObjectId(treeId) || !CommitSignatureFormat.IsObjectId(baseTree)
            || (baseCommit is not null && !CommitSignatureFormat.IsObjectId(baseCommit)))
            return new(false, "tree_mismatch", "The request does not carry well-formed tree ids, so nothing can be rebuilt.");
        if (files.Count == 0)
            return new(false, "tree_mismatch", "The request lists no files, so there is nothing to rebuild.");
        if (files.Count > MaxFiles)
            return new(false, "too_many_files", $"The request lists {files.Count} files; at most {MaxFiles} can be rebuilt.");

        var missing = FilesWithoutContent(files);
        if (missing.Count > 0)
        {
            return new(false, "missing_content",
                $"{missing.Count} file(s) are listed without their content (binary, oversized or over the bundle limit), so the tree "
                + "cannot be rebuilt from this request and it cannot be approved.", MissingContent: missing);
        }
        var unsupported = files.Where(file => file.Change is not ("added" or "modified" or "deleted" or "renamed" or "copied")).ToList();
        if (unsupported.Count > 0)
        {
            return new(false, "unsupported_change",
                $"{unsupported.Count} file(s) have a change kind this version cannot rebuild ({unsupported[0].Change}), so the request cannot be approved.");
        }
        var submodules = files.Where(file => file.Mode == ReviewFileMode.GitLink || file.PreviousMode == ReviewFileMode.GitLink).ToList();
        if (submodules.Count > 0)
        {
            return new(false, "unsupported_change",
                $"{submodules.Count} file(s) are submodule entries ({submodules[0].Path}), which name a commit this repository may not have, so the tree cannot be rebuilt from this request and it cannot be approved.");
        }
        if (ShapeProblem(files) is { } inconsistent)
            return new(false, "inconsistent_change", $"The request describes its files inconsistently: {inconsistent} It cannot be approved.");

        var format = await GitCli.RunAsync(repositoryPath, ["rev-parse", "--show-object-format"], cancellationToken, GitTimeout, 1024);
        if (!format.Succeeded)
            return new(false, "not_a_repository", $"{repositoryPath} is not a Git repository this VibeRails can read, so the tree cannot be rebuilt there.");
        var sha256 = format.StdOut.Trim() == "sha256";
        var idLength = sha256 ? 64 : 40;
        if (treeId.Length != idLength || baseTree.Length != idLength)
            return new(false, "tree_mismatch", $"The request's tree ids are not {(sha256 ? "SHA-256" : "SHA-1")} ids like {repositoryPath}'s, so it was captured from a different repository.");

        if (baseCommit is null)
        {
            if (baseTree != (sha256 ? EmptyTreeSha256 : EmptyTreeSha1))
                return new(false, "base_commit_missing", "The request names no base commit, so the base of the change cannot be verified here.");
        }
        else
        {
            var exists = await GitCli.RunAsync(repositoryPath, ["cat-file", "-e", baseCommit + "^{commit}"], cancellationToken, GitTimeout, 1024);
            if (!exists.Succeeded)
                return new(false, "base_commit_missing", $"Your copy of the repository does not have the base commit {baseCommit}. Fetch it (git fetch) and open the request again.");
            var commitTree = await GitCli.RunAsync(repositoryPath, ["rev-parse", baseCommit + "^{tree}"], cancellationToken, GitTimeout, 1024);
            if (!commitTree.Succeeded || commitTree.StdOut.Trim() != baseTree)
                return new(false, "base_tree_mismatch", $"The request's base tree is not the tree of its base commit {baseCommit}, so the request is inconsistent and cannot be approved.");
        }

        var indexPath = Path.Combine(Path.GetTempPath(), "viberails-review-" + Guid.NewGuid().ToString("N") + ".index");
        var environment = new Dictionary<string, string>(StringComparer.Ordinal) { ["GIT_INDEX_FILE"] = indexPath };
        try
        {
            var read = await GitCli.RunAsync(repositoryPath, ["read-tree", baseTree], cancellationToken, GitTimeout, 4096, environment);
            if (!read.Succeeded)
                return new(false, "git_failed", $"Git could not load the base tree {baseTree} from your repository ({Trim(read.StdErr)}).");

            var baseEntries = await ReadBaseEntriesAsync(repositoryPath, environment, files, cancellationToken);
            if (baseEntries is null)
                return new(false, "git_failed", "Git could not list the base tree's entries, so the base of each file is unknown.");
            if (VerifyAgainstBase(files, baseEntries, sha256) is { } refused)
                return refused;

            var input = BuildIndexInfo(files, baseEntries, sha256);
            var update = await GitCli.RunAsync(repositoryPath, ["update-index", "-z", "--index-info"], cancellationToken, GitTimeout, 4096, environment, input);
            if (!update.Succeeded)
                return new(false, "git_failed", $"Git could not apply the request's files to the temporary index ({Trim(update.StdErr)}).");

            var written = await GitCli.RunAsync(repositoryPath, ["write-tree", "--missing-ok"], cancellationToken, GitTimeout, 4096, environment);
            if (!written.Succeeded)
                return new(false, "git_failed", $"Git could not write the rebuilt tree ({Trim(written.StdErr)}).");
            var rebuilt = written.StdOut.Trim();

            // The complete transition: what the rebuilt tree changes relative to the base, path by path and
            // kind by kind, must be what the request lists. An added path that collides with a base
            // directory or file makes update-index drop the conflicting base entries without a word (Codex
            // slice D re-reviews), turning an unlisted file, or one listed as modified, into a deletion the
            // reviewer never saw. Trees only; no blob is read.
            var touched = await GitCli.RunAsync(
                repositoryPath, ["diff-tree", "-r", "-z", "--name-status", "--no-renames", baseTree, rebuilt],
                cancellationToken, GitTimeout, null, environment);
            if (!touched.Succeeded)
                return new(false, "git_failed", $"Git could not list what the rebuilt tree changes ({Trim(touched.StdErr)}).");
            var mismatches = TransitionMismatches(files, touched.StdOut, file => ModifiedStatus(file, baseEntries, sha256));
            if (mismatches.Count > 0)
            {
                var named = string.Join("; ", mismatches.Take(5)) + (mismatches.Count > 5 ? $"; and {mismatches.Count - 5} more" : string.Empty);
                return new(false, "hidden_change",
                    $"The change the request's files make differs from the one they describe in {mismatches.Count} path(s): {named}. "
                    + "A path can be deleted by an addition that overlaps it, or changed without being listed, so what you read is not "
                    + "the whole change. Do not approve it.", rebuilt);
            }

            if (rebuilt == treeId)
                return new(true, "matches", $"The files in the request rebuild tree {treeId} from base {baseCommit ?? baseTree}.", rebuilt);
            var modes = files.Any(file => file.Mode is not null)
                ? "Either the request does not show everything in that tree, or something differs that the bundle does not carry."
                : "Either the request does not show everything in that tree, or it was captured by an older VibeRails that carries no file modes, "
                  + "so a new executable or a symlink cannot be placed as it was staged.";
            return new(false, "tree_mismatch",
                $"The files in the request do not rebuild the tree it asks you to sign (rebuilt {rebuilt}, requested {treeId}). "
                + modes + " Do not approve it; ask the requester to send it again.", rebuilt);
        }
        finally
        {
            foreach (var path in new[] { indexPath, indexPath + ".lock" })
            {
                try { File.Delete(path); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* a temporary file; nothing reads it again */ }
            }
        }
    }

    /// <summary>
    /// The base entries (mode and blob id) at every path the bundle names, read from the temporary index
    /// in bounded batches of literal pathspecs: the source of each change, and the new path of an added
    /// or renamed file so its absence can be checked. A path the base does not have is simply absent.
    /// Null when git failed.
    /// </summary>
    private static async Task<Dictionary<string, ReviewBaseEntry>?> ReadBaseEntriesAsync(
        string repositoryPath,
        IReadOnlyDictionary<string, string> environment,
        IReadOnlyList<ReviewBundleFile> files,
        CancellationToken cancellationToken)
    {
        var wanted = files
            .SelectMany(file => file.PreviousPath is { } previous && previous != file.Path ? [previous, file.Path] : new[] { file.Path })
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var entries = new Dictionary<string, ReviewBaseEntry>(StringComparer.Ordinal);
        var batch = new List<string>();
        var batchChars = 0;
        foreach (var path in wanted)
        {
            if (batch.Count > 0 && (batch.Count >= PathsPerListing || batchChars + path.Length > CharsPerListing))
            {
                if (!await ListAsync(batch)) return null;
                batch.Clear();
                batchChars = 0;
            }
            batch.Add(path);
            batchChars += path.Length + 1;
        }
        if (batch.Count > 0 && !await ListAsync(batch)) return null;
        return entries;

        async Task<bool> ListAsync(List<string> paths)
        {
            var listing = await GitCli.RunAsync(
                repositoryPath,
                ["--literal-pathspecs", "ls-files", "--stage", "-z", "--", .. paths],
                cancellationToken, GitTimeout, null, environment);
            if (!listing.Succeeded) return false;
            foreach (var entry in listing.StdOut.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                // "<mode> <object> <stage>\t<path>"
                var tab = entry.IndexOf('\t');
                if (tab <= 0) continue;
                var fields = entry[..tab].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length != 3 || fields[2] != "0") continue;
                entries[entry[(tab + 1)..]] = new ReviewBaseEntry(fields[0], fields[1]);
            }
            return true;
        }
    }

    /// <summary>
    /// The canonical shape of one file transition, as the capture writes it and as the reviewer reads it:
    /// an added file has no base text and no previous path; a deleted file has no staged text and no
    /// previous path; a modified file has no previous path; a renamed or copied file names a distinct
    /// previous path; no path is listed twice and no rename source is also a listed path. Anything else
    /// lets a side the renderer shows be something other than the transition the tree proves (Codex
    /// review of slice D, F1: an added file carrying <c>Before = After</c> reads as "no text changes").
    /// Shared by the desktop parser, the popup and the rebuild; null when the shape is canonical.
    /// </summary>
    public static string? ShapeProblem(IReadOnlyList<ReviewBundleFile> files)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var renameSources = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            var previous = file.PreviousPath;
            if (file.Change is "renamed" or "copied")
            {
                if (previous is null || previous == file.Path)
                    return $"{file.Path} is listed as {file.Change} without a distinct previous path.";
                if (file.Change == "renamed" && !renameSources.Add(previous))
                    return $"{previous} is the source of more than one rename.";
            }
            else if (previous is not null && previous != file.Path)
            {
                return $"{file.Path} is listed as {file.Change} but names a previous path ({previous}).";
            }
            if (file.Change == "added" && file.Before is not null)
                return $"{file.Path} is listed as added but shows base text.";
            if (file.Change == "deleted" && file.After is not null)
                return $"{file.Path} is listed as deleted but shows staged text.";
            // Carried bytes stand in for text that is not shown, never beside it (VIBE-211).
            if (file.AfterBytes is not null && file.Omitted is null)
                return $"{file.Path} carries staged bytes but is not listed without text.";
            if (file.AfterBytes is not null && file.Change == "deleted")
                return $"{file.Path} is listed as deleted but carries staged bytes.";
            if (file.Mode is not null && !ReviewFileMode.IsKnown(file.Mode))
                return $"{file.Path} carries an unknown mode ({file.Mode}).";
            if (file.PreviousMode is not null && !ReviewFileMode.IsKnown(file.PreviousMode))
                return $"{file.Path} carries an unknown base mode ({file.PreviousMode}).";
            if (file.Change == "deleted" && file.Mode is not null)
                return $"{file.Path} is listed as deleted but carries a staged mode.";
            if (file.Change == "added" && file.PreviousMode is not null)
                return $"{file.Path} is listed as added but carries a base mode.";
            if (!paths.Add(file.Path))
                return $"{file.Path} is listed more than once.";
        }
        var overlap = renameSources.FirstOrDefault(paths.Contains);
        return overlap is null ? null : $"{overlap} is both the source of a rename and a listed file.";
    }

    /// <summary>
    /// Binds what the reviewer read to the base commit: a changed, renamed, copied or deleted file's
    /// source must exist in the base and its shown base text must hash to the base blob; a file listed
    /// as added, and the destination of a rename or copy, must not exist there (an existing destination
    /// would be overwritten with its base content never shown). A file shown without base text, and not
    /// marked as omitted, cannot be checked and is refused too. Null when every file agrees with the base.
    /// </summary>
    internal static ReviewTreeRebuildResult? VerifyAgainstBase(
        IReadOnlyList<ReviewBundleFile> files, IReadOnlyDictionary<string, ReviewBaseEntry> baseEntries, bool sha256)
    {
        foreach (var file in files)
        {
            var moves = file.Change is "renamed" or "copied";
            var source = moves ? file.PreviousPath! : file.Path;
            if (file.Change == "added" || moves)
            {
                if (baseEntries.ContainsKey(file.Path))
                {
                    return new(false, "base_mismatch",
                        $"{file.Path} is listed as {file.Change}, but the base commit already has that file, so the request misdescribes the change"
                        + (moves ? " and hides what the destination contained" : string.Empty) + ". Do not approve it.");
                }
                if (!moves) continue;
            }
            if (!baseEntries.TryGetValue(source, out var entry))
            {
                return new(false, "base_mismatch",
                    $"{source} is listed as {file.Change}, but the base commit has no such file, so the request misdescribes the change. Do not approve it.");
            }
            if (file.PreviousMode is { } shownMode && shownMode != entry.Mode)
            {
                return new(false, "base_mismatch",
                    $"The base mode shown for {source} ({shownMode}) is not the base commit's ({entry.Mode}), so the change the reviewer read is not the real one. Do not approve it.");
            }
            if (file.Before is null)
            {
                // Omitted content is shown as omitted; nothing is claimed about the base text.
                if (file.Omitted is not null) continue;
                return new(false, "base_text_missing",
                    $"{source} is shown without its base text, so the change the reviewer read cannot be checked against the base commit. Do not approve it.");
            }
            if (BlobId(file.Before, sha256) != entry.ObjectId)
            {
                return new(false, "base_mismatch",
                    $"The base text shown for {source} is not what the base commit contains, so the diff the reviewer read is not the real change. Do not approve it.");
            }
        }
        return null;
    }

    /// <summary>
    /// Where the transition the rebuilt tree makes differs from the one the bundle lists: a touched path
    /// the bundle does not name (the deletions a path collision caused), a listed path the rebuild left
    /// unchanged, or a path whose kind of change differs (listed as modified, actually deleted, because
    /// an overlapping addition removed it). Compares <c>diff-tree -r -z --name-status --no-renames base
    /// rebuilt</c> with what the listed kinds imply: a rename is a deletion and an addition, a copy an
    /// addition, a modification what <paramref name="modifiedStatus"/> says (null for an unchanged file;
    /// by default <c>M</c> when the shown text differs). Sorted, for the message.
    /// </summary>
    internal static IReadOnlyList<string> TransitionMismatches(
        IReadOnlyList<ReviewBundleFile> files, string nameStatusZ, Func<ReviewBundleFile, string?>? modifiedStatus = null)
    {
        modifiedStatus ??= file => file.Before != file.After ? "M" : null;
        var expected = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            switch (file.Change)
            {
                case "added": expected[file.Path] = "A"; break;
                case "deleted": expected[file.Path] = "D"; break;
                case "modified": if (modifiedStatus(file) is { } status) expected[file.Path] = status; break;
                case "renamed": expected[file.PreviousPath!] = "D"; expected[file.Path] = "A"; break;
                case "copied": expected[file.Path] = "A"; break;
            }
        }
        var actual = new Dictionary<string, string>(StringComparer.Ordinal);
        var fields = nameStatusZ.Split('\0');
        // "<status>\0<path>\0" pairs; with --no-renames every status has exactly one path.
        for (var i = 0; i + 1 < fields.Length; i += 2)
        {
            if (fields[i + 1].Length > 0) actual[fields[i + 1]] = fields[i].Length > 0 ? fields[i][..1] : "?";
        }
        var problems = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (path, status) in actual)
        {
            if (!expected.TryGetValue(path, out var listed))
                problems.Add($"{path} ({DescribeStatus(status)}, not listed)");
            else if (listed != status)
                problems.Add($"{path} (listed as {DescribeStatus(listed)}, actually {DescribeStatus(status)})");
        }
        foreach (var (path, listed) in expected)
        {
            if (!actual.ContainsKey(path)) problems.Add($"{path} (listed as {DescribeStatus(listed)}, unchanged)");
        }
        return [.. problems];
    }

    private static string DescribeStatus(string status) => status switch
    {
        "A" => "added", "D" => "deleted", "M" => "modified", "T" => "type changed", _ => "changed"
    };

    /// <summary>
    /// What <c>diff-tree --name-status</c> reports for a file listed as modified once its staged bytes and
    /// mode are placed over the base entry: nothing when blob and mode are the base's, <c>T</c> when the
    /// entry changes type (a file becoming a symlink), otherwise <c>M</c>, which covers a mode-only change.
    /// A file whose base entry or bytes are unknown is expected to show as modified; the base and content
    /// checks before this have already refused it.
    /// </summary>
    internal static string? ModifiedStatus(ReviewBundleFile file, IReadOnlyDictionary<string, ReviewBaseEntry> baseEntries, bool sha256)
    {
        if (!baseEntries.TryGetValue(file.Path, out var entry) || !TryStagedBytes(file, out var bytes)) return "M";
        var mode = file.Mode ?? entry.Mode;
        if (EntryType(mode) != EntryType(entry.Mode)) return "T";
        return mode != entry.Mode || BlobId(bytes, sha256) != entry.ObjectId ? "M" : null;
    }

    private static string EntryType(string mode) => mode switch
    {
        ReviewFileMode.Symlink => "symlink",
        ReviewFileMode.GitLink => "gitlink",
        _ => "file"
    };

    /// <summary>
    /// The <c>update-index --index-info</c> input: a removal (mode 0) for every deleted path and every
    /// rename source, then an entry for every path with staged content whose blob id is the hash of
    /// its exact bytes: the staged text encoded again (capture decoded strict UTF-8, so byte-order mark
    /// and line endings come back), or the carried bytes decoded. The mode is the carried staged mode;
    /// a bundle without modes places a new file as plain and keeps a changed file's base mode.
    /// </summary>
    internal static byte[] BuildIndexInfo(IReadOnlyList<ReviewBundleFile> files, IReadOnlyDictionary<string, ReviewBaseEntry> baseEntries, bool sha256)
    {
        var zero = new string('0', sha256 ? 64 : 40);
        var builder = new MemoryStream();
        void Line(string mode, string objectId, string path)
        {
            builder.Write(Encoding.UTF8.GetBytes(mode + " " + objectId + "\t" + path));
            builder.WriteByte(0);
        }
        foreach (var file in files)
        {
            if (file.Change == "deleted")
                Line("0", zero, file.PreviousPath ?? file.Path);
            else if (file.Change == "renamed" && file.PreviousPath is { } source && source != file.Path)
                Line("0", zero, source);
        }
        foreach (var file in files)
        {
            if (file.Change == "deleted" || !TryStagedBytes(file, out var bytes)) continue;
            var mode = file.Mode
                ?? (file.Change == "added" ? PlainFileMode
                    : baseEntries.TryGetValue(file.PreviousPath ?? file.Path, out var known) ? known.Mode : PlainFileMode);
            Line(mode, BlobId(bytes, sha256), file.Path);
        }
        return builder.ToArray();
    }

    /// <summary>The id git gives a blob with this exact text as UTF-8.</summary>
    internal static string BlobId(string content, bool sha256) => BlobId(Encoding.UTF8.GetBytes(content), sha256);

    /// <summary>The id git gives a blob with this exact content: the hash of <c>blob &lt;length&gt;\0</c> and the bytes.</summary>
    internal static string BlobId(ReadOnlySpan<byte> bytes, bool sha256)
    {
        var header = Encoding.ASCII.GetBytes("blob " + bytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\0");
        using IncrementalHash hash = sha256
            ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256)
            : IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        hash.AppendData(header);
        hash.AppendData(bytes);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static string Trim(string stderr)
    {
        var line = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "no detail";
        return line.Length > 200 ? line[..200] + "..." : line;
    }
}

/// <summary>One entry of the base tree as the temporary index lists it: its mode and blob id.</summary>
internal sealed record ReviewBaseEntry(string Mode, string ObjectId);
