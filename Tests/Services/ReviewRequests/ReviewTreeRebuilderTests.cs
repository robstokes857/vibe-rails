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
        await File.WriteAllTextAsync(Path.Combine(Repo, "plain.txt"), "unchanged text\n", Ct);
        Directory.CreateDirectory(Path.Combine(Repo, "tools"));
        await File.WriteAllTextAsync(Path.Combine(Repo, "tools", "run.sh"), "#!/bin/sh\necho hi\n", Ct);
        await Git(Repo, "add", "-A", "--", ".");
        await Git(Repo, "update-index", "--chmod=+x", "tools/run.sh");
        await Git(Repo, "-c", "core.hooksPath=", "commit", "-q", "-m", "baseline");
        // The reviewer's clone has the base commit and nothing of the change staged below.
        await Git(_root, "clone", "-q", Repo, Clone);

        await File.WriteAllBytesAsync(Path.Combine(Repo, "crlf.txt"), Encoding.UTF8.GetBytes("line one\r\nline two\r\nline three\r\n"), Ct);
        await File.WriteAllBytesAsync(Path.Combine(Repo, "bom.txt"), [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("hello <world> & 'friends'\n")], Ct);
        await File.WriteAllBytesAsync(Path.Combine(Repo, "logo.bin"), LogoBytes, Ct);
        await File.WriteAllTextAsync(Path.Combine(Repo, "tools", "run.sh"), "#!/bin/sh\necho changed\n", Ct);
        File.Move(Path.Combine(Repo, "keep.cs"), Path.Combine(Repo, "kept.cs"));
        File.Delete(Path.Combine(Repo, "old.md"));
        await Git(Repo, "add", "-A", "--", ".");
        // Linux git add refreshes the mode from the non-executable fixture on disk.
        // This staged fixture represents a content edit that retains its executable bit.
        await Git(Repo, "update-index", "--chmod=+x", "tools/run.sh");
    }

    /// <summary>A binary file: NUL bytes, every byte value, and base64 that needs padding and contains '+' and '/'.</summary>
    private static readonly byte[] LogoBytes = [0x89, 0x50, 0x4E, 0x47, 0x00, 0x00, .. Enumerable.Range(0, 256).Select(i => (byte)i), 0xFF, 0xFE, 0x00];

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
        // CRLF, a byte-order mark, a deletion, a rename, a modified executable and a binary file are all in that tree.
        Assert.Contains(bundle.Files, file => file.Path == "crlf.txt" && file.After!.Contains("\r\n", StringComparison.Ordinal));
        Assert.Contains(bundle.Files, file => file.Path == "bom.txt" && file.After![0] == '﻿');
        Assert.Contains(bundle.Files, file => file.Path == "old.md" && file.Change == "deleted");
        // VIBE-211: the binary file is listed without text and carries its exact bytes and mode; the modified
        // executable and the deleted file carry their modes too, and nothing carries bytes beside text.
        var logo = bundle.Files.Single(file => file.Path == "logo.bin");
        Assert.Equal(("added", true, ReviewBundleOmission.Binary, null, null, "100644", null),
            (logo.Change, logo.Binary, logo.Omitted, logo.Before, logo.After, logo.Mode, logo.PreviousMode));
        Assert.Equal(Convert.ToBase64String(LogoBytes), logo.AfterBytes);
        Assert.True(ReviewBundleCapture.TryDecodeBytes(logo.AfterBytes, ReviewBundleCapture.MaxFileBytes, out var decoded));
        Assert.Equal(LogoBytes, decoded);
        Assert.Equal(LogoBytes.Length, ReviewBundleCapture.DecodedLength(logo.AfterBytes));
        var run = bundle.Files.Single(file => file.Path == "tools/run.sh");
        Assert.Equal(("100755", "100755"), (run.Mode, run.PreviousMode));
        var old = bundle.Files.Single(file => file.Path == "old.md");
        Assert.Equal((null, "100644"), (old.Mode, old.PreviousMode));
        var kept = bundle.Files.Single(file => file.Path == "kept.cs");
        Assert.Equal(("renamed", "100644", "100644"), (kept.Change, kept.Mode, kept.PreviousMode));
        Assert.All(bundle.Files.Where(file => file.After is not null), file => Assert.Null(file.AfterBytes));
        Assert.Empty(ReviewTreeRebuilder.FilesWithoutContent(bundle.Files));
        // The clone's own index and working tree are untouched: the rebuild used a temporary index only.
        Assert.Equal(string.Empty, (await Git(Clone, "status", "--porcelain")).Trim());
        Assert.False(File.Exists(Path.Combine(Clone, "bom.txt")));
    }

    /// <summary>VIBE-211: carried bytes are hashed like text, so altering them is a tree mismatch, and dropping them leaves the request unapprovable.</summary>
    [Fact]
    public async Task TamperedOrMissingBytesAreRefused()
    {
        var (_, bundle) = await CaptureAsync();
        var tampered = bundle.Files
            .Select(file => file.Path == "logo.bin" ? file with { AfterBytes = Convert.ToBase64String([.. LogoBytes, 0x00]) } : file)
            .ToList();
        var result = await ReviewTreeRebuilder.RebuildAsync(Clone, bundle.TreeId, bundle.BaseTree, bundle.BaseCommit, tampered, Ct);
        Assert.Equal("tree_mismatch", result.Code);
        Assert.NotEqual(bundle.TreeId, result.RebuiltTree);

        var dropped = bundle.Files.Select(file => file.Path == "logo.bin" ? file with { AfterBytes = null } : file).ToList();
        result = await ReviewTreeRebuilder.RebuildAsync(Clone, bundle.TreeId, bundle.BaseTree, bundle.BaseCommit, dropped, Ct);
        Assert.Equal("missing_content", result.Code);
        Assert.Equal(["logo.bin"], result.MissingContent);

        var unreadable = bundle.Files.Select(file => file.Path == "logo.bin" ? file with { AfterBytes = "not base64!" } : file).ToList();
        Assert.Equal(["logo.bin"], ReviewTreeRebuilder.FilesWithoutContent(unreadable));
        Assert.Equal("missing_content", (await ReviewTreeRebuilder.RebuildAsync(Clone, bundle.TreeId, bundle.BaseTree, bundle.BaseCommit, unreadable, Ct)).Code);

        // Bytes beside text, or on a deleted file, are an inconsistent shape, never a second content source.
        Assert.Equal("crlf.txt carries staged bytes but is not listed without text.",
            ReviewTreeRebuilder.ShapeProblem(bundle.Files.Select(file => file.Path == "crlf.txt" ? file with { AfterBytes = logoBase64() } : file).ToList()));
        Assert.Equal("old.md is listed as deleted but carries staged bytes.",
            ReviewTreeRebuilder.ShapeProblem(bundle.Files.Select(file => file.Path == "old.md" ? file with { AfterBytes = logoBase64(), Omitted = ReviewBundleOmission.Binary, Before = null } : file).ToList()));
        static string logoBase64() => Convert.ToBase64String(LogoBytes);
    }

    /// <summary>VIBE-211: a text file over the per-file text bound is listed oversized and travels as bytes instead, so it rebuilds.</summary>
    [Fact]
    public async Task AnOversizedTextFileTravelsAsBytesAndRebuilds()
    {
        var big = string.Concat(Enumerable.Repeat("a line of generated text that is long enough to add up quickly\n", 5000));
        Assert.True(Encoding.UTF8.GetByteCount(big) > ReviewBundleCapture.MaxFileContentBytes);
        await File.WriteAllTextAsync(Path.Combine(Repo, "big.txt"), big, Ct);
        await Git(Repo, "add", "--", "big.txt");
        var (_, bundle) = await CaptureAsync();

        var file = bundle.Files.Single(f => f.Path == "big.txt");
        Assert.Equal((ReviewBundleOmission.Oversized, false, null), (file.Omitted, file.Binary, file.After));
        Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes(big)), file.AfterBytes);
        var result = await ReviewTreeRebuilder.RebuildAsync(Clone, bundle.TreeId, bundle.BaseTree, bundle.BaseCommit, bundle.Files, Ct);
        Assert.True(result.Matches, result.Message);
    }

    /// <summary>VIBE-211: a file over the per-file byte bound stays listed without content, so such a request is still refused rather than guessed.</summary>
    [Fact]
    public async Task AFileOverTheByteBoundStaysWithoutContent()
    {
        var snapshot = await new GitStagedSnapshotProvider().CaptureAsync(Repo, Ct);
        var bundle = await ReviewBundleCapture.BuildAsync(snapshot, ReviewBundleCapture.BaseTextReader(snapshot),
            (path, _) => Task.FromResult<byte[]?>(null), [], ["reviewer@example.test"], null, null, DateTime.UtcNow, Ct);

        var logo = bundle.Files.Single(file => file.Path == "logo.bin");
        Assert.Equal((ReviewBundleOmission.Binary, null, "100644"), (logo.Omitted, logo.AfterBytes, logo.Mode));
        Assert.Equal(["logo.bin"], ReviewTreeRebuilder.FilesWithoutContent(bundle.Files));
        Assert.Equal("missing_content", (await ReviewTreeRebuilder.RebuildAsync(Clone, bundle.TreeId, bundle.BaseTree, bundle.BaseCommit, bundle.Files, Ct)).Code);
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
        // The full misdescription: an added file carries no base text and no base mode (a base mode left in place is refused as inconsistent first).
        var misdescribed = bundle.Files
            .Select(file => file.Path == "crlf.txt" ? file with { Change = "added", Before = null, PreviousPath = null, PreviousMode = null } : file)
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
    /// VIBE-211: the bundle carries each file's mode, so a new executable is placed as staged and the tree
    /// matches. A bundle from an older VibeRails carries none, rebuilds it as a plain file and is refused,
    /// not guessed: the reviewer cannot tell a mode bit from a hidden change.
    /// </summary>
    [Fact]
    public async Task ANewExecutableRebuildsWithItsCarriedModeAndIsAMismatchWithout()
    {
        await File.WriteAllTextAsync(Path.Combine(Repo, "tools", "new.sh"), "#!/bin/sh\necho new\n", Ct);
        await Git(Repo, "add", "--", "tools/new.sh");
        await Git(Repo, "update-index", "--chmod=+x", "tools/new.sh");
        var (_, bundle) = await CaptureAsync();
        var fresh = bundle.Files.Single(file => file.Path == "tools/new.sh");
        Assert.Equal(("added", "100755", null), (fresh.Change, fresh.Mode, fresh.PreviousMode));

        var result = await ReviewTreeRebuilder.RebuildAsync(Clone, bundle.TreeId, bundle.BaseTree, bundle.BaseCommit, bundle.Files, Ct);
        Assert.True(result.Matches, result.Message);

        var older = bundle.Files.Select(file => file with { Mode = null, PreviousMode = null }).ToList();
        result = await ReviewTreeRebuilder.RebuildAsync(Clone, bundle.TreeId, bundle.BaseTree, bundle.BaseCommit, older, Ct);
        Assert.Equal("tree_mismatch", result.Code);
        Assert.Contains("older VibeRails", result.Message);
        Assert.Contains("modes", result.Message);
    }

    /// <summary>
    /// VIBE-211: a mode change on an existing file (with or without a text change) rebuilds with the carried
    /// modes and is expected as a modification; a carried base mode that is not the base commit's is refused
    /// like forged base text, because the reviewer read a mode transition that is not the real one.
    /// </summary>
    [Fact]
    public async Task AModeChangeRebuildsAndAForgedBaseModeIsRefused()
    {
        await Git(Repo, "update-index", "--chmod=+x", "crlf.txt");  // text and mode change
        await Git(Repo, "update-index", "--chmod=+x", "plain.txt"); // mode-only change
        var (_, bundle) = await CaptureAsync();
        var crlf = bundle.Files.Single(file => file.Path == "crlf.txt");
        Assert.Equal(("modified", "100755", "100644"), (crlf.Change, crlf.Mode, crlf.PreviousMode));
        var plain = bundle.Files.Single(file => file.Path == "plain.txt");
        Assert.Equal(("modified", "100755", "100644", true), (plain.Change, plain.Mode, plain.PreviousMode, plain.Before == plain.After));

        var result = await ReviewTreeRebuilder.RebuildAsync(Clone, bundle.TreeId, bundle.BaseTree, bundle.BaseCommit, bundle.Files, Ct);
        Assert.True(result.Matches, result.Message);

        var forged = bundle.Files.Select(file => file.Path == "crlf.txt" ? file with { PreviousMode = "100755" } : file).ToList();
        result = await ReviewTreeRebuilder.RebuildAsync(Clone, bundle.TreeId, bundle.BaseTree, bundle.BaseCommit, forged, Ct);
        Assert.Equal("base_mismatch", result.Code);
        Assert.Contains("base mode shown for crlf.txt", result.Message);

        // Shape: modes belong to the side that exists.
        Assert.Equal("old.md is listed as deleted but carries a staged mode.",
            ReviewTreeRebuilder.ShapeProblem(bundle.Files.Select(file => file.Path == "old.md" ? file with { Mode = "100644" } : file).ToList()));
        Assert.Equal("bom.txt is listed as added but carries a base mode.",
            ReviewTreeRebuilder.ShapeProblem(bundle.Files.Select(file => file.Path == "bom.txt" ? file with { PreviousMode = "100644" } : file).ToList()));
        Assert.Equal("bom.txt carries an unknown mode (100600).",
            ReviewTreeRebuilder.ShapeProblem(bundle.Files.Select(file => file.Path == "bom.txt" ? file with { Mode = "100600" } : file).ToList()));
        // A submodule entry cannot be rebuilt here: it names a commit, not bytes.
        var gitlink = bundle.Files.Select(file => file.Path == "bom.txt" ? file with { Mode = ReviewFileMode.GitLink } : file).ToList();
        Assert.Null(ReviewTreeRebuilder.ShapeProblem(gitlink));
        Assert.Equal("unsupported_change", (await ReviewTreeRebuilder.RebuildAsync(Clone, bundle.TreeId, bundle.BaseTree, bundle.BaseCommit, gitlink, Ct)).Code);
    }

    [Fact]
    public void ModifiedStatusFollowsBlobAndModeAgainstTheBase()
    {
        var baseEntries = new Dictionary<string, ReviewBaseEntry>(StringComparer.Ordinal)
        {
            ["same.txt"] = new("100644", ReviewTreeRebuilder.BlobId("same\n", false)),
            ["link"] = new("100644", ReviewTreeRebuilder.BlobId("target\n", false))
        };
        var same = new ReviewBundleFile("same.txt", "modified", false, null, 0, "same\n", "same\n", null, null, "100644", "100644");
        Assert.Null(ReviewTreeRebuilder.ModifiedStatus(same, baseEntries, false));
        Assert.Equal("M", ReviewTreeRebuilder.ModifiedStatus(same with { Mode = "100755" }, baseEntries, false));
        Assert.Equal("M", ReviewTreeRebuilder.ModifiedStatus(same with { After = "other\n" }, baseEntries, false));
        // Bytes-only content is compared by blob id, not by text equality.
        var bytes = same with { Before = null, After = null, Omitted = ReviewBundleOmission.Oversized, AfterBytes = Convert.ToBase64String(Encoding.UTF8.GetBytes("same\n")) };
        Assert.Null(ReviewTreeRebuilder.ModifiedStatus(bytes, baseEntries, false));
        Assert.Equal("M", ReviewTreeRebuilder.ModifiedStatus(bytes with { AfterBytes = Convert.ToBase64String(Encoding.UTF8.GetBytes("changed\n")) }, baseEntries, false));
        // A file becoming a symlink is a type change in git's eyes.
        var link = new ReviewBundleFile("link", "modified", false, null, 0, "target\n", "target\n", null, null, "120000", "100644");
        Assert.Equal("T", ReviewTreeRebuilder.ModifiedStatus(link, baseEntries, false));
        // A bundle without modes falls back to the base mode.
        Assert.Null(ReviewTreeRebuilder.ModifiedStatus(same with { Mode = null, PreviousMode = null }, baseEntries, false));
    }

    [Fact]
    public async Task BlobIdsMatchGitForExactBytes()
    {
        var content = "﻿line one\r\nline two\r\n<c> & 'd' é\n";
        var viaGit = await GitCli.RunAsync(Repo, ["hash-object", "--stdin", "--no-filters"], Ct, standardInput: Encoding.UTF8.GetBytes(content));
        Assert.True(viaGit.Succeeded, viaGit.StdErr);

        Assert.Equal(viaGit.StdOut.Trim(), ReviewTreeRebuilder.BlobId(content, sha256: false));
        Assert.Equal("e69de29bb2d1d6434b8b29ae775ad8c2e48c5391", ReviewTreeRebuilder.BlobId(string.Empty, sha256: false));

        // VIBE-211: bytes hash the same way, with no text decoding in between.
        var viaGitBytes = await GitCli.RunAsync(Repo, ["hash-object", "--stdin", "--no-filters"], Ct, standardInput: LogoBytes);
        Assert.True(viaGitBytes.Succeeded, viaGitBytes.StdErr);
        Assert.Equal(viaGitBytes.StdOut.Trim(), ReviewTreeRebuilder.BlobId(LogoBytes, sha256: false));
    }

    [Fact]
    public void IndexInfoRemovesDeletionsAndRenameSourcesAndPlacesCarriedModesAndBytes()
    {
        var files = new List<ReviewBundleFile>
        {
            new("kept.cs", "renamed", false, "keep.cs", 0, "class Keep { }\n", "class Keep { }\n", null),
            new("old.md", "deleted", false, null, 1, "# Old\n", null, null),
            new("tools/run.sh", "modified", false, null, 1, "a\n", "b\n", null),
            new("new.txt", "added", false, null, 1, null, "new\n", null),
            // VIBE-211: a carried mode wins over the fallback; bytes stand in for text.
            new("tools/new.sh", "added", false, null, 1, null, "#!/bin/sh\n", null, null, "100755", null),
            new("logo.bin", "added", true, null, null, null, null, ReviewBundleOmission.Binary, Convert.ToBase64String(LogoBytes), "100644", null),
            new("tools/run.sh.bak", "modified", false, null, 1, "a\n", "b\n", null, null, "100644", "100755"),
            new("absent.bin", "added", true, null, null, null, null, ReviewBundleOmission.Binary) // no bytes: nothing to place
        };
        var modes = new Dictionary<string, ReviewBaseEntry>(StringComparer.Ordinal)
        {
            ["keep.cs"] = new("100644", new string('1', 40)),
            ["tools/run.sh"] = new("100755", new string('2', 40)),
            ["tools/run.sh.bak"] = new("100755", new string('3', 40))
        };

        var lines = Encoding.UTF8.GetString(ReviewTreeRebuilder.BuildIndexInfo(files, modes, sha256: false)).Split('\0', StringSplitOptions.RemoveEmptyEntries);

        var zero = new string('0', 40);
        Assert.Equal(
            [
                $"0 {zero}\tkeep.cs",
                $"0 {zero}\told.md",
                $"100644 {ReviewTreeRebuilder.BlobId("class Keep { }\n", false)}\tkept.cs",
                $"100755 {ReviewTreeRebuilder.BlobId("b\n", false)}\ttools/run.sh",
                $"100644 {ReviewTreeRebuilder.BlobId("new\n", false)}\tnew.txt",
                $"100755 {ReviewTreeRebuilder.BlobId("#!/bin/sh\n", false)}\ttools/new.sh",
                $"100644 {ReviewTreeRebuilder.BlobId(LogoBytes, false)}\tlogo.bin",
                $"100644 {ReviewTreeRebuilder.BlobId("b\n", false)}\ttools/run.sh.bak"
            ],
            lines);
    }

    [Fact]
    public void CarriedBytesAreDecodedOnlyWithinTheBound()
    {
        Assert.True(ReviewBundleCapture.TryDecodeBytes(Convert.ToBase64String(LogoBytes), LogoBytes.Length, out var exact));
        Assert.Equal(LogoBytes, exact);
        Assert.False(ReviewBundleCapture.TryDecodeBytes(Convert.ToBase64String(LogoBytes), LogoBytes.Length - 1, out _));
        Assert.False(ReviewBundleCapture.TryDecodeBytes("", 10, out _));
        Assert.False(ReviewBundleCapture.TryDecodeBytes(null, 10, out _));
        Assert.False(ReviewBundleCapture.TryDecodeBytes("abc", 10, out _));
        Assert.False(ReviewBundleCapture.TryDecodeBytes("====", 10, out _));
        Assert.False(ReviewBundleCapture.TryDecodeBytes("ab!=", 10, out _));
        Assert.Equal(0, ReviewBundleCapture.DecodedLength(null));
        foreach (var length in new[] { 1, 2, 3, 4, 5, 300 })
            Assert.Equal(length, ReviewBundleCapture.DecodedLength(Convert.ToBase64String(new byte[length])));
    }

    private async Task<(string Tree, ReviewBundle Bundle)> CaptureAsync()
    {
        var tree = (await Git(Repo, "write-tree")).Trim();
        var snapshot = await new GitStagedSnapshotProvider().CaptureAsync(Repo, Ct);
        var bundle = await ReviewBundleCapture.BuildAsync(snapshot, ReviewBundleCapture.BaseTextReader(snapshot),
            ReviewBundleCapture.StagedBytesReader(snapshot), [], ["reviewer@example.test"], null, null, DateTime.UtcNow, Ct);
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
