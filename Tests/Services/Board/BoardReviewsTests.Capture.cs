using VibeRails.DTOs;
using VibeRails.Services.Board;
using Xunit;

namespace Tests.Services.Board;

public sealed partial class BoardReviewsTests
{
    [Fact]
    public async Task CaptureRangeWithDeletionHasStableFingerprint()
    {
        await Git("rm", "sample.cs");
        await Git("-c", "core.hooksPath=", "commit", "-m", "delete sample");
        var head = await Git("rev-parse", "HEAD");

        var capture = await BoardReviewService.CaptureAsync(repo, "range", baseline, head, false, Ct);
        var repeated = await BoardReviewService.CaptureAsync(repo, "range", baseline, head, false, Ct);

        Assert.NotNull(capture.Hash);
        Assert.Equal(capture.Hash, repeated.Hash);
        Assert.Equal(baseline, capture.Base);
        Assert.Equal(head, capture.Head);
        Assert.Null(capture.Limitations);
        Assert.Equal("range: Deleted sample.cs", Assert.Single(capture.Files!));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CaptureWorkingTreeWithDeletionTracksRestorationAndDeletedPath(bool staged)
    {
        await File.WriteAllTextAsync(Path.Combine(repo, "other.cs"), "class Other {}\n", Ct);
        await Git("add", ".");
        await Git("-c", "core.hooksPath=", "commit", "-m", "second file");
        File.Delete(Path.Combine(repo, "sample.cs"));
        if (staged) await Git("add", "-u");

        var capture = await BoardReviewService.CaptureAsync(repo, "working-tree", null, null, true, Ct);
        var repeated = await BoardReviewService.CaptureAsync(repo, "working-tree", null, null, true, Ct);
        Assert.NotNull(capture.Hash);
        Assert.Equal(capture.Hash, repeated.Hash);
        Assert.Null(capture.Limitations);
        Assert.Equal("working-tree: Deleted sample.cs", Assert.Single(capture.Files!));

        await Git("restore", "--source=HEAD", "--staged", "--worktree", "sample.cs");
        var restored = await BoardReviewService.CaptureAsync(repo, "working-tree", null, null, true, Ct);
        Assert.NotNull(restored.Hash);
        Assert.NotEqual(capture.Hash, restored.Hash);

        File.Delete(Path.Combine(repo, "other.cs"));
        if (staged) await Git("add", "-u");
        var otherDeletion = await BoardReviewService.CaptureAsync(repo, "working-tree", null, null, true, Ct);
        Assert.NotNull(otherDeletion.Hash);
        Assert.NotEqual(capture.Hash, otherDeletion.Hash);
    }

    [Fact]
    public async Task RangeAndDirtyDeletionsCanPassReviewAndBecomeStaleAfterRestoration()
    {
        await File.WriteAllTextAsync(Path.Combine(repo, "dirty.cs"), "class Dirty {}\n", Ct);
        await Git("add", ".");
        await Git("-c", "core.hooksPath=", "commit", "-m", "second file");
        var rangeBase = await Git("rev-parse", "HEAD");
        await Git("rm", "sample.cs");
        await Git("-c", "core.hooksPath=", "commit", "-m", "delete sample");
        var head = await Git("rev-parse", "HEAD");
        File.Delete(Path.Combine(repo, "dirty.cs"));

        var flow = await Workflow();
        var session = flow.Session;
        var begun = await reviews.BeginAsync(repo, card.Id, session, repo, "range", "Committed and dirty deletions", rangeBase, head, true, Ct);
        Assert.NotNull(begun.SnapshotHash);
        Assert.Null(begun.CaptureLimitations);
        Assert.Contains("range: Deleted sample.cs", begun.ScopeFiles!);
        Assert.Contains("dirty: Deleted dirty.cs", begun.ScopeFiles!);
        var saved = await reviews.SaveAsync(repo, card.Id, session, begun.Id, "No findings reported", "None", "Reviewed deletions", "None", Ct);
        Assert.Equal("No findings reported", saved.Result);
        Assert.StartsWith("Current", (await reviews.ReportAsync(repo, card.Key, begun.Id, true, repo, Ct))!.Freshness);
        await new BoardWorkflowService(store, reviews).ReportAsync(repo, card.Id, repo,
            BoardAuthor.Agent("Codex", "codex", session), "passed", "Deletions reviewed", null, begun.Id, Ct);
        await jobs.CompleteRunAsync(flow.RunId, JobRunStatus.Succeeded, 0, null, Ct);
        Assert.Equal("Passed", (await store.GetLaneAutomationStatusesAsync(repo, card.Id, Ct))[0].StepStatus);

        await Git("restore", "dirty.cs");
        Assert.StartsWith("Stale", (await reviews.ReportAsync(repo, card.Key, begun.Id, true, repo, Ct))!.Freshness);
    }

    [Theory]
    [InlineData("range", "binary")]
    [InlineData("range", "gitlink")]
    [InlineData("working-tree", "binary")]
    [InlineData("working-tree", "gitlink")]
    public async Task CaptureWithDeletionStillRejectsBinaryOrGitlink(string scope, string contentKind)
    {
        await Git("rm", "sample.cs");
        if (contentKind == "gitlink")
        {
            Directory.CreateDirectory(Path.Combine(repo, "vendor"));
            await Git("update-index", "--add", "--cacheinfo", $"160000,{baseline},vendor");
        }
        else
        {
            await File.WriteAllBytesAsync(Path.Combine(repo, "binary.dat"), [0, 1, 2, 3], Ct);
            await Git("add", "binary.dat");
        }
        if (scope == "range") await Git("-c", "core.hooksPath=", "commit", "-m", "delete and add unreadable content");
        var head = await Git("rev-parse", "HEAD");

        var capture = await BoardReviewService.CaptureAsync(repo, scope, baseline, head, false, Ct);

        Assert.Null(capture.Hash);
        Assert.Contains("Binary or unreadable content", capture.Limitations);
        Assert.DoesNotContain("Inputs changed", capture.Limitations!);
        Assert.Contains($"{scope}: Deleted sample.cs", capture.Files!);
    }
}
