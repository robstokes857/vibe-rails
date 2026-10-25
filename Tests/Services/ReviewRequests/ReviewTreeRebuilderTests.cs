using System.Text;
using VibeRails.Services.Git;
using VibeRails.Services.GitPreflight;
using VibeRails.Services.ReviewRequests;
using Xunit;

namespace Tests.Services.ReviewRequests;

/// <summary>
/// VB-50 design decision 7: the reviewer's machine proves that a bundle's files make up the tree an
/// approval signs. A real repository plays the requester (staged change captured by the real bundle
/// capture) and a real clone plays the reviewer (has the base commit, never saw the staged change).
/// </summary>
public sealed class ReviewTreeRebuilderTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "review-rebuild-" + Guid.NewGuid().ToString("N"));
    private string Repo => Path.Combine(_root, "repo");
    private string Clone => Path.Combine(_root, "clone");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(Repo);
        await Git(Repo, "init", "-q");
        await Git(Repo, "config", "user.email", "rebuild@example.invalid");
        await Git(Repo, "config", "user.name", "Rebuild");
        await Git(Repo, "config", "core.autocrlf", "false");
        await Git(Repo, "config", "commit.gpgsign", "false");
        await File.WriteAllTextAsync(Path.Combine(Repo, "keep.cs"), "class Keep { }\n", Ct);
        await File.WriteAllBytesAsync(Path.Combine(Repo, "crlf.txt"), Encoding.UTF8.GetBytes("line one\r\nline two\r\n"), Ct);
        await File.WriteAllTextAsync(Path.Combine(Repo, "old.md"), "# Old\n", Ct);
        Directory.CreateDirectory(Path.Combine(Repo, "tools"));
        await File.WriteAllTextAsync(Path.Combine(Repo, "tools", "run.sh"), "#!/bin/sh\necho hi\n", Ct);
        await Git(Repo, "add", "-A", "--", ".");
        await Git(Repo, "update-index", "--chmod=+x", "tools/run.sh");
        await Git(Repo, "-c", "core.hooksPath=", "commit", "-q", "-m", "baseline");
        // The reviewer's clone has the base commit and nothing of the change staged below.
        await Git(_root, "clone", "-q", Repo, Clone);

        await File.WriteAllBytesAsync(Path.Combine(Repo, "crlf.txt"), Encoding.UTF8.GetBytes("line one\r\nline two\r\nline three\r\n"), Ct);
        await File.WriteAllBytesAsync(Path.Combine(Repo, "bom.txt"), [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("hello <world> & 'friends'\n")], Ct);
        await File.WriteAllTextAsync(Path.Combine(Repo, "tools", "run.sh"), "#!/bin/sh\necho changed\n", Ct);
        File.Move(Path.Combine(Repo, "keep.cs"), Path.Combine(Repo, "kept.cs"));
        File.Delete(Path.Combine(Repo, "old.md"));
        await Git(Repo, "add", "-A", "--", ".");
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException) { }
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task TheCapturedBundleRebuildsTheStagedTreeInTheReviewersClone()
    {
        var (tree, bundle) = await CaptureAsync();

        var result = await ReviewTreeRebuilder.RebuildAsync(Clone, bundle.TreeId, bundle.BaseTree, bundle.BaseCommit, bundle.Files, Ct);

        Assert.True(result.Matches, result.Message);
        Assert.Equal(("matches", tree), (result.Code, result.RebuiltTree));
        // CRLF, a byte-order mark, a deletion, a rename and a modified executable are all in that tree.
        Assert.Contains(bundle.Files, file => file.Path == "crlf.txt" && file.After!.Contains("\r\n", StringComparison.Ordinal));
        Assert.Contains(bundle.Files, file => file.Path == "bom.txt" && file.After![0] == '﻿');
        Assert.Contains(bundle.Files, file => file.Path == "old.md" && file.Change == "deleted");
        // The clone's own index and working tree are untouched: the rebuild used a temporary index only.
        Assert.Equal(string.Empty, (await Git(Clone, "status", "--porcelain")).Trim());
        Assert.False(File.Exists(Path.Combine(Clone, "bom.txt")));
    }

    [Fact]
    public async Task TamperedTextIsAMismatchAndNeverMatches()
    {
        var (_, bundle) = await CaptureAsync();
        var tampered = bundle.Files
            .Select(file => file.Path == "crlf.txt" ? file with { After = file.After!.Replace("three", "3", StringComparison.Ordinal) } : file)
            .ToList();

        var result = await ReviewTreeRebuilder.RebuildAsync(Clone, bundle.TreeId, bundle.BaseTree, bundle.BaseCommit, tampered, Ct);

        Assert.False(result.Matches);
        Assert.Equal("tree_mismatch", result.Code);
        Assert.NotNull(result.RebuiltTree);
        Assert.NotEqual(bundle.TreeId, result.RebuiltTree);
        Assert.Contains("Do not approve it", result.Message);
    }

    /// <summary>
    /// Codex review of slice D, F1: with Before set to the staged text the hosted diff reads "no text
    /// changes" while the tree still matches. The base text is now bound to the base commit's blob.
    /// </summary>
    [Fact]
    public async Task ForgedBaseTextIsRefusedEvenThoughTheTreeWouldMatch()
    {
        var (_, bundle) = await CaptureAsync();
        var forged = bundle.Files
            .Select(file => file.Path == "crlf.txt" ? file with { Before = file.After, ChangedLines = 0 } : file)
            .ToList();

        var result = await ReviewTreeRebuilder.RebuildAsync(Clone, bundle.TreeId, bundle.BaseTree, bundle.BaseCommit, forged, Ct);

        Assert.Equal("base_mismatch", result.Code);
        Assert.Contains("crlf.txt", result.Message);
        Assert.Contains("not the real change", result.Message);
        Assert.Null(result.RebuiltTree);
    }

    /// <summary>
    /// Codex re-review of slice D, F1 variants: an added file carrying base text, a deleted file carrying
    /// staged text, a modified file whose previous path names another base file, and a rename onto an
    /// existing destination all rendered as "no text changes" while the tree still matched.
    /// </summary>
    [Fact]
    public async Task AnAddedFileCarryingBaseTextIsInconsistent()
    {
        var (_, bundle) = await CaptureAsync();
        var forged = bundle.Files
            .Select(file => file.Path == "bom.txt" ? file with { Before = file.After, ChangedLines = 0 } : file)
            .ToList();

        var result = await ReviewTreeRebuilder.RebuildAsync(Clone, bundle.TreeId, bundle.BaseTree, bundle.BaseCommit, forged, Ct);

        Assert.Equal("inconsistent_change", result.Code);
        Assert.Contains("bom.txt is listed as added but shows base text", result.Message);
    }

    [Fact]
    public async Task ADeletedFileCarryingStagedTextIsInconsistent()
    {
        var (_, bundle) = await CaptureAsync();
        var forged = bundle.Files
            .Select(file => file.Path == "old.md" ? file with { After = file.Before, ChangedLines = 0 } : file)
            .ToList();

        var result = await ReviewTreeRebuilder.RebuildAsync(Clone, bundle.TreeId, bundle.BaseTree, bundle.BaseCommit, forged, Ct);

        Assert.Equal("inconsistent_change", result.Code);
        Assert.Contains("old.md is listed as deleted but shows staged text", result.Message);
    }

    [Fact]
    public async Task AModifiedFileWhosePreviousPathNamesAnotherBaseFileIsInconsistent()
    {
        var (_, bundle) = await CaptureAsync();
        // crlf.txt's real base is shown as some other base file's content: Before hashes to keep.cs's blob.
        var forged = bundle.Files
            .Select(file => file.Path == "crlf.txt" ? file with { PreviousPath = "keep.cs", Before = "class Keep { }\n" } : file)
            .ToList();

        var result = await ReviewTreeRebuilder.RebuildAsync(Clone, bundle.TreeId, bundle.BaseTree, bundle.BaseCommit, forged, Ct);

        Assert.Equal("inconsistent_change", result.Code);
        Assert.Contains("crlf.txt is listed as modified but names a previous path", result.Message);
    }

    [Fact]
    public async Task ARenameOntoAnExistingDestinationIsRefused()
    {
        var (_, bundle) = await CaptureAsync();
        // The real change deletes old.md and modifies crlf.txt. Listing it as "old.md renamed to crlf.txt"
        // rebuilds the same tree while hiding what crlf.txt contained before.
        var crlf = bundle.Files.Single(file => file.Path == "crlf.txt");
        var forged = bundle.Files
            .Where(file => file.Path is not ("old.md" or "crlf.txt"))
            .Append(new ReviewBundleFile("crlf.txt", "renamed", false, "old.md", crlf.ChangedLines, "# Old\n", crlf.After, null))
            .ToList();

        var result = await ReviewTreeRebuilder.RebuildAsync(Clone, bundle.TreeId, bundle.BaseTree, bundle.BaseCommit, forged, Ct);

        Assert.Equal("base_mismatch", result.Code);
        Assert.Contains("hides what the destination contained", result.Message);
    }

    /// <summary>
    /// Codex third review of slice D: an added path that collides with a base directory makes
    /// update-index drop the directory's entries without a word. The honest capture lists those
    /// deletions; a bundle that omits them rebuilds the same tree and must still be refused.
    /// </summary>
    [Fact]
    public async Task AFileReplacingABaseDirectoryMustListTheDirectorysDeletions()
    {
        await Git(Repo, "rm", "-q", "-r", "-f", "--", "tools"); // -f: tools/run.sh already has a staged edit in this fixture.
        await File.WriteAllTextAsync(Path.Combine(Repo, "tools"), "now a file\n", Ct);
        await Git(Repo, "add", "--", "tools");
        var (_, bundle) = await CaptureAsync();
        Assert.Contains(bundle.Files, file => file.Path == "tools/run.sh" && file.Change == "deleted");
        Assert.True((await ReviewTreeRebuilder.RebuildAsync(Clone, bundle.TreeId, bundle.BaseTree, bundle.BaseCommit, bundle.Files, Ct)).Matches);

        var forged = bundle.Files.Where(file => file.Path != "tools/run.sh").ToList();
        var result = await ReviewTreeRebuilder.RebuildAsync(Clone, bundle.TreeId, bundle.BaseTree, bundle.BaseCommit, forged, Ct);

        Assert.Equal("hidden_change", result.Code);
        Assert.Contains("tools/run.sh", result.Message);
        Assert.Equal(bundle.TreeId, result.RebuiltTree); // The tree would have matched: the deletion was implicit.
    }

    [Fact]
    public async Task ADirectoryReplacingABaseFileMustListTheFilesDeletion()
    {
        // old.md is already deleted in the staged change; a directory of that name now holds a new file.
        Directory.CreateDirectory(Path.Combine(Repo, "old.md"));
        await File.WriteAllTextAsync(Path.Combine(Repo, "old.md", "new.txt"), "inside\n", Ct);
        await Git(Repo, "add", "--", "old.md/new.txt");
        var (_, bundle) = await CaptureAsync();
        Assert.True((await ReviewTreeRebuilder.RebuildAsync(Clone, bundle.TreeId, bundle.BaseTree, bundle.BaseCommit, bundle.Files, Ct)).Matches);

        var forged = bundle.Files.Where(file => file.Path != "old.md").ToList();
        var result = await ReviewTreeRebuilder.RebuildAsync(Clone, bundle.TreeId, bundle.BaseTree, bundle.BaseCommit, forged, Ct);

        Assert.Equal("hidden_change", result.Code);
        Assert.Contains("old.md", result.Message);
    }

    /// <summary>
    /// Codex fifth review: a path listed as modified while an overlapping addition deletes it rebuilds the
    /// true tree and names every touched path, yet shows a modification where the tree has a deletion.
    /// </summary>
    [Fact]
    public async Task AModificationHiddenByAnOverlappingAdditionIsRefused()
    {
        await Git(Repo, "rm", "-q", "-f", "--", "crlf.txt");
        Directory.CreateDirectory(Path.Combine(Repo, "crlf.txt"));
        await File.WriteAllTextAsync(Path.Combine(Repo, "crlf.txt", "new.txt"), "inside\n", Ct);
        await Git(Repo, "add", "--", "crlf.txt/new.txt");
        var (_, bundle) = await CaptureAsync();
        Assert.True((await ReviewTreeRebuilder.RebuildAsync(Clone, bundle.TreeId, bundle.BaseTree, bundle.BaseCommit, bundle.Files, Ct)).Matches);

        var forged = bundle.Files
            .Select(file => file.Path == "crlf.txt" && file.Change == "deleted"
                ? file with { Change = "modified", After = file.Before + "# harmless\r\n", ChangedLines = 1 }
                : file)
            .ToList();
        var result = await ReviewTreeRebuilder.RebuildAsync(Clone, bundle.TreeId, bundle.BaseTree, bundle.BaseCommit, forged, Ct);

        Assert.Equal("hidden_change", result.Code);
        Assert.Contains("crlf.txt (listed as modified, actually deleted)", result.Message);
        Assert.Equal(bundle.TreeId, result.RebuiltTree); // Every path was listed and the tree matched; only the kind lied.
    }

    [Fact]
    public void TransitionMismatchesNameWhatDiffersFromTheListedChanges()
    {
        var files = new List<ReviewBundleFile>
        {
            new("kept.cs", "renamed", false, "keep.cs", 0, "x\n", "x\n", null),
            new("tools", "added", false, null, 1, null, "now a file\n", null),
            new("same.txt", "modified", false, null, 0, "same\n", "same\n", null) // Unchanged text: nothing expected.
        };

        Assert.Empty(ReviewTreeRebuilder.TransitionMismatches(files, "D\0keep.cs\0A\0kept.cs\0A\0tools\0"));
        Assert.Equal(["nested/hidden-security.txt (deleted, not listed)", "tools/run.sh (deleted, not listed)"],
            ReviewTreeRebuilder.TransitionMismatches(files, "D\0keep.cs\0A\0kept.cs\0A\0tools\0D\0tools/run.sh\0D\0nested/hidden-security.txt\0"));
        Assert.Equal(["tools (listed as added, actually deleted)"],
            ReviewTreeRebuilder.TransitionMismatches(files, "D\0keep.cs\0A\0kept.cs\0D\0tools\0"));
        Assert.Equal(["kept.cs (listed as added, unchanged)", "tools (listed as added, unchanged)"],
            ReviewTreeRebuilder.TransitionMismatches(files, "D\0keep.cs\0"));
        Assert.Equal(["keep.cs (listed as deleted, unchanged)", "kept.cs (listed as added, unchanged)", "tools (listed as added, unchanged)"],
            ReviewTreeRebuilder.TransitionMismatches(files, string.Empty));
    }

    [Fact]
    public void DuplicateAndOverlappingTransitionsAreInconsistent()
    {
        var a = new ReviewBundleFile("a.txt", "modified", false, null, 1, "x\n", "y\n", null);
        Assert.Equal("a.txt is listed more than once.", ReviewTreeRebuilder.ShapeProblem([a, a]));
        var renamed = new ReviewBundleFile("b.txt", "renamed", false, "a.txt", 0, "x\n", "x\n", null);
        Assert.Equal("a.txt is both the source of a rename and a listed file.", ReviewTreeRebuilder.ShapeProblem([renamed, a]));
        Assert.Equal("a.txt is the source of more than one rename.", ReviewTreeRebuilder.ShapeProblem([renamed, renamed with { Path = "c.txt" }]));
        Assert.Equal("b.txt is listed as renamed without a distinct previous path.", ReviewTreeRebuilder.ShapeProblem([renamed with { PreviousPath = null }]));
        // A copy's source legitimately stays and may itself be modified.
        var copied = new ReviewBundleFile("b.txt", "copied", false, "a.txt", 0, "x\n", "x\n", null);
        Assert.Null(ReviewTreeRebuilder.ShapeProblem([copied, a]));
        // A deleted file whose previous path repeats its own path is the capture's own shape.
        Assert.Null(ReviewTreeRebuilder.ShapeProblem([new ReviewBundleFile("gone.md", "deleted", false, "gone.md", 1, "x\n", null, null)]));
    }

    [Fact]
    public async Task AChangedFileListedAsAddedIsRefused()
    {
        var (_, bundle) = await CaptureAsync();
        var misdescribed = bundle.Files
            .Select(file => file.Path == "crlf.txt" ? file with { Change = "added", Before = null, PreviousPath = null } : file)
            .ToList();

        var result = await ReviewTreeRebuilder.RebuildAsync(Clone, bundle.TreeId, bundle.BaseTree, bundle.BaseCommit, misdescribed, Ct);

        Assert.Equal("base_mismatch", result.Code);
        Assert.Contains("listed as added", result.Message);
    }

    [Fact]
    public async Task ADeletedFileWithForgedBaseTextIsRefused()
    {
        var (_, bundle) = await CaptureAsync();
        var forged = bundle.Files
            .Select(file => file.Path == "old.md" ? file with { Before = "# Something harmless\n" } : file)
            .ToList();

        var result = await ReviewTreeRebuilder.RebuildAsync(Clone, bundle.TreeId, bundle.BaseTree, bundle.BaseCommit, forged, Ct);

        Assert.Equal("base_mismatch", result.Code);
        Assert.Contains("old.md", result.Message);
    }

    [Fact]
    public async Task AChangedFileShownWithoutBaseTextIsRefused()
    {
        var (_, bundle) = await CaptureAsync();
        var blind = bundle.Files
            .Select(file => file.Path == "crlf.txt" ? file with { Before = null } : file)
            .ToList();

        var result = await ReviewTreeRebuilder.RebuildAsync(Clone, bundle.TreeId, bundle.BaseTree, bundle.BaseCommit, blind, Ct);

        Assert.Equal("base_text_missing", result.Code);
        Assert.Contains("crlf.txt", result.Message);
    }

    [Fact]
    public async Task AFileWithoutContentCannotBeApproved()
    {
        var (_, bundle) = await CaptureAsync();
        var omitted = bundle.Files
            .Select(file => file.Path == "bom.txt" ? file with { Before = null, After = null, Binary = true, Omitted = ReviewBundleOmission.Binary } : file)
            .ToList();

        var result = await ReviewTreeRebuilder.RebuildAsync(Clone, bundle.TreeId, bundle.BaseTree, bundle.BaseCommit, omitted, Ct);

        Assert.Equal("missing_content", result.Code);
        Assert.Equal(["bom.txt"], result.MissingContent);
        Assert.Contains("cannot be approved", result.Message);
    }

    [Fact]
    public async Task ABaseCommitTheReviewerLacksIsRefused()
    {
        var (_, bundle) = await CaptureAsync();
        var elsewhere = Path.Combine(_root, "unrelated");
        Directory.CreateDirectory(elsewhere);
        await Git(elsewhere, "init", "-q");

        var result = await ReviewTreeRebuilder.RebuildAsync(elsewhere, bundle.TreeId, bundle.BaseTree, bundle.BaseCommit, bundle.Files, Ct);

        Assert.Equal("base_commit_missing", result.Code);
        Assert.Contains(bundle.BaseCommit!, result.Message);
        Assert.Contains("git fetch", result.Message);
    }

    [Fact]
    public async Task NotARepositoryIsRefused()
    {
        var (_, bundle) = await CaptureAsync();
        var plain = Path.Combine(_root, "plain");
        Directory.CreateDirectory(plain);

        var result = await ReviewTreeRebuilder.RebuildAsync(plain, bundle.TreeId, bundle.BaseTree, bundle.BaseCommit, bundle.Files, Ct);

        Assert.Equal("not_a_repository", result.Code);
    }

    /// <summary>
    /// The bundle carries no file modes, so a new executable rebuilds as a plain file and the tree
    /// differs. That is a refusal, not a guess: the reviewer cannot tell a mode bit from a hidden change.
    /// </summary>
    [Fact]
    public async Task ANewExecutableIsAMismatchBecauseTheBundleCarriesNoModes()
    {
        await File.WriteAllTextAsync(Path.Combine(Repo, "tools", "new.sh"), "#!/bin/sh\necho new\n", Ct);
        await Git(Repo, "add", "--", "tools/new.sh");
        await Git(Repo, "update-index", "--chmod=+x", "tools/new.sh");
        var (_, bundle) = await CaptureAsync();

        var result = await ReviewTreeRebuilder.RebuildAsync(Clone, bundle.TreeId, bundle.BaseTree, bundle.BaseCommit, bundle.Files, Ct);

        Assert.Equal("tree_mismatch", result.Code);
        Assert.Contains("mode", result.Message);
    }

    [Fact]
    public async Task BlobIdsMatchGitForExactBytes()
    {
        var content = "﻿line one\r\nline two\r\n<c> & 'd' é\n";
        var viaGit = await GitCli.RunAsync(Repo, ["hash-object", "--stdin", "--no-filters"], Ct, standardInput: Encoding.UTF8.GetBytes(content));
        Assert.True(viaGit.Succeeded, viaGit.StdErr);

        Assert.Equal(viaGit.StdOut.Trim(), ReviewTreeRebuilder.BlobId(content, sha256: false));
        Assert.Equal("e69de29bb2d1d6434b8b29ae775ad8c2e48c5391", ReviewTreeRebuilder.BlobId(string.Empty, sha256: false));
    }

    [Fact]
    public void IndexInfoRemovesDeletionsAndRenameSourcesAndKeepsBaseModes()
    {
        var files = new List<ReviewBundleFile>
        {
            new("kept.cs", "renamed", false, "keep.cs", 0, "class Keep { }\n", "class Keep { }\n", null),
            new("old.md", "deleted", false, null, 1, "# Old\n", null, null),
            new("tools/run.sh", "modified", false, null, 1, "a\n", "b\n", null),
            new("new.txt", "added", false, null, 1, null, "new\n", null)
        };
        var modes = new Dictionary<string, ReviewBaseEntry>(StringComparer.Ordinal)
        {
            ["keep.cs"] = new("100644", new string('1', 40)),
            ["tools/run.sh"] = new("100755", new string('2', 40))
        };

        var lines = Encoding.UTF8.GetString(ReviewTreeRebuilder.BuildIndexInfo(files, modes, sha256: false)).Split('\0', StringSplitOptions.RemoveEmptyEntries);

        var zero = new string('0', 40);
        Assert.Equal(
            [
                $"0 {zero}\tkeep.cs",
                $"0 {zero}\told.md",
                $"100644 {ReviewTreeRebuilder.BlobId("class Keep { }\n", false)}\tkept.cs",
                $"100755 {ReviewTreeRebuilder.BlobId("b\n", false)}\ttools/run.sh",
                $"100644 {ReviewTreeRebuilder.BlobId("new\n", false)}\tnew.txt"
            ],
            lines);
    }

    private async Task<(string Tree, ReviewBundle Bundle)> CaptureAsync()
    {
        var tree = (await Git(Repo, "write-tree")).Trim();
        var snapshot = await new GitStagedSnapshotProvider().CaptureAsync(Repo, Ct);
        var bundle = await ReviewBundleCapture.BuildAsync(snapshot, ReviewBundleCapture.BaseTextReader(snapshot), [],
            ["reviewer@example.test"], null, null, DateTime.UtcNow, Ct);
        Assert.Equal(tree, bundle.TreeId);
        Assert.NotNull(bundle.BaseCommit);
        return (tree, bundle);
    }

    private static async Task<string> Git(string directory, params string[] arguments)
    {
        var result = await GitCli.RunAsync(directory, arguments, Ct, TimeSpan.FromSeconds(30));
        Assert.True(result.Succeeded, $"git {string.Join(' ', arguments)}: {result.StdErr}");
        return result.StdOut;
    }
}
