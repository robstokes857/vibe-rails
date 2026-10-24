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
/// <c>not_a_repository</c>, <c>base_commit_missing</c>, <c>base_tree_mismatch</c>, <c>tree_mismatch</c>, <c>git_failed</c>.</param>
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
/// What this cannot know, and therefore refuses rather than guesses: a file listed without content
/// (binary, oversized, over the bundle limit) has no bytes to hash; a base commit the reviewer does not
/// have cannot be read; a changed file mode is not in the bundle, so a new file is assumed to be a plain
/// file (<c>100644</c>) and a changed file keeps the mode its base entry had. A mismatch for any reason
/// is a refusal: the reviewer cannot tell a hidden change from a mode bit, and must not sign either.
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

    /// <summary>Files whose staged content is not in the bundle. A deleted file needs none: it has no staged content.</summary>
    public static IReadOnlyList<string> FilesWithoutContent(IReadOnlyList<ReviewBundleFile> files) =>
        files.Where(file => file.Change != "deleted" && file.After is null).Select(file => file.Path).ToList();

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
        var unsupported = files.Where(file => file.Change is not ("added" or "modified" or "deleted" or "renamed" or "copied")).Select(file => file.Path).ToList();
        if (unsupported.Count > 0)
        {
            return new(false, "unsupported_change",
                $"{unsupported.Count} file(s) have a change kind this version cannot rebuild ({files.First(f => unsupported.Contains(f.Path)).Change}), so the request cannot be approved.");
        }

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

            var baseModes = await ReadBaseModesAsync(repositoryPath, environment, files, cancellationToken);
            if (baseModes is null)
                return new(false, "git_failed", "Git could not list the base tree's entries, so the file modes are unknown.");

            var input = BuildIndexInfo(files, baseModes, sha256);
            var update = await GitCli.RunAsync(repositoryPath, ["update-index", "-z", "--index-info"], cancellationToken, GitTimeout, 4096, environment, input);
            if (!update.Succeeded)
                return new(false, "git_failed", $"Git could not apply the request's files to the temporary index ({Trim(update.StdErr)}).");

            var written = await GitCli.RunAsync(repositoryPath, ["write-tree", "--missing-ok"], cancellationToken, GitTimeout, 4096, environment);
            if (!written.Succeeded)
                return new(false, "git_failed", $"Git could not write the rebuilt tree ({Trim(written.StdErr)}).");
            var rebuilt = written.StdOut.Trim();
            if (rebuilt == treeId)
                return new(true, "matches", $"The files in the request rebuild tree {treeId} from base {baseCommit ?? baseTree}.", rebuilt);
            return new(false, "tree_mismatch",
                $"The files in the request do not rebuild the tree it asks you to sign (rebuilt {rebuilt}, requested {treeId}). "
                + "Either the request does not show everything in that tree, or something the bundle cannot carry differs, such as a file "
                + "mode. Do not approve it; ask the requester to send it again.", rebuilt);
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
    /// The modes of the base entries the bundle changes or renames away from, read from the temporary
    /// index in bounded batches of literal pathspecs. A path the base does not have is simply absent.
    /// Null when git failed.
    /// </summary>
    private static async Task<Dictionary<string, string>?> ReadBaseModesAsync(
        string repositoryPath,
        IReadOnlyDictionary<string, string> environment,
        IReadOnlyList<ReviewBundleFile> files,
        CancellationToken cancellationToken)
    {
        var wanted = files
            .Where(file => file.Change is "modified" or "renamed" or "copied")
            .Select(file => file.PreviousPath ?? file.Path)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var modes = new Dictionary<string, string>(StringComparer.Ordinal);
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
        return modes;

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
                modes[entry[(tab + 1)..]] = fields[0];
            }
            return true;
        }
    }

    /// <summary>
    /// The <c>update-index --index-info</c> input: a removal (mode 0) for every deleted path and every
    /// rename source, then an entry for every path with staged content whose blob id is the hash of
    /// its exact bytes. The staged text was decoded as strict UTF-8 by the capture, so encoding it again
    /// gives the staged bytes back, byte-order mark and line endings included.
    /// </summary>
    internal static byte[] BuildIndexInfo(IReadOnlyList<ReviewBundleFile> files, IReadOnlyDictionary<string, string> baseModes, bool sha256)
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
            if (file.Change == "deleted" || file.After is null) continue;
            var mode = file.Change == "added" ? PlainFileMode
                : baseModes.TryGetValue(file.PreviousPath ?? file.Path, out var known) ? known : PlainFileMode;
            Line(mode, BlobId(file.After, sha256), file.Path);
        }
        return builder.ToArray();
    }

    /// <summary>The id git gives a blob with this exact content: the hash of <c>blob &lt;length&gt;\0</c> and the bytes.</summary>
    internal static string BlobId(string content, bool sha256)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
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
