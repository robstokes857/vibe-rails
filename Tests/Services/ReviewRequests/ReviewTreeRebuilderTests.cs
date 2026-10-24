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
