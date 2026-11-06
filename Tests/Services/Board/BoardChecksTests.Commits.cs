using VibeRails.DTOs;
using VibeRails.Services.Board;
using Xunit;

namespace Tests.Services.Board;

public sealed partial class BoardChecksTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CardWithoutCodeNeedsReviewExceptResearch(bool research)
    {
        if (research)
            card = (await store.UpdateCardAsync(repo, card.Id,
                new(Type: BoardCardTypes.ResearchSpike), Ct))!;
        await Write("unrelated.cs", "class Unrelated {}\n");
        var check = await Run(JobActionKind.CodeQuality, "card");
        Assert.Equal(research ? "Skipped/not applicable" : "Needs review", check.Status);
        Assert.Contains("no code attached to this card", check.Summary);
        Assert.Equal(0, check.AnalyzedCount);
        Assert.Null(check.Evidence);
        Assert.Equal(check.Status, (await store.GetCheckAsync(repo, card.Id, check.Id, Ct))!.Status);
    }

    [Fact]
    public async Task ExplicitCommitPinsItsOwnChangesIncludingRootAndIgnoresDirtyCheckout()
    {
        await Write("example.cs", "class Changed {}\n");
        var sha = await Commit();
        await Write("example.cs", "class Other {}\n");
        await Write("unrelated.cs", "class Unrelated {}\n");
        var before = await Git("status", "--porcelain");
        var rootCheck = await Run(JobActionKind.CodeQuality, "commit", baseline[..8]);
        Assert.Contains("Original", Assert.Single(rootCheck.Evidence!.Files).AddedContent);
        var check = await Run(JobActionKind.CodeQuality, "commit", sha[..8]);
        Assert.Equal(sha, check.HeadCommit);
        Assert.Equal(baseline, check.BaseCommit);
        Assert.Equal(1, check.AnalyzedCount);
        Assert.Contains("Changed", Assert.Single(check.Evidence!.Files).AddedContent);
        Assert.Equal(new[] { sha }, check.Evidence.CommitShas);
        Assert.Equal(before, await Git("status", "--porcelain"));
        Assert.StartsWith("Captured inputs match", await Engine().FreshnessAsync(repo, check, Ct));
    }

    [Fact]
    public async Task CardCommitsExcludeInterleavedFilesAndLinesAndFreezeExactSelection()
    {
        await Write("example.cs", "class Original {}\nclass First {}\n");
        var first = await Commit();
        await Write("example.cs", "class Original {}\nclass First {}\nclass Unrelated {}\n");
        await Write("unrelated.cs", "class OtherSession {}\n");
        var unrelated = await Commit();
        await Write("example.cs", "class Original {}\nclass First {}\nclass Unrelated {}\nclass Last {}\n");
        var last = await Commit();
        await Link(first);
        await Link(last);
        await Write("example.cs", "class Dirty {}\n");
        await Write("dirty.cs", "class Dirty {}\n");

        var raw = await Engine().CaptureAsync(repo, new("range", baseline, last), Ct);
        Assert.Equal(new[] { 2, 3, 4 }, Assert.Single(raw.Files, f => f.RelativePath == "example.cs").AddedLineNumbers);

        var check = await Run(JobActionKind.CodeQuality, "card");

        Assert.Equal("Passed", check.Status);
        Assert.Equal(1, check.AnalyzedCount);
        Assert.Equal(1, check.FileCount);
        var file = Assert.Single(check.Evidence!.Files);
        Assert.Equal("example.cs", file.Path);
        Assert.Contains("First", file.AddedContent);
        Assert.Contains("Last", file.AddedContent);
        Assert.DoesNotContain("Unrelated", file.AddedContent);
        Assert.DoesNotContain("Original", file.AddedContent);
        Assert.Equal(new[] { 2, 4 }, file.AddedLineNumbers);
        var stored = (await store.GetCheckEvidenceAsync(repo, card.Id, check.Id, Ct))!;
        Assert.Equal(check.Evidence.CommitShas, stored.CommitShas);
        Assert.StartsWith("Captured inputs match", await Engine().FreshnessAsync(repo, check, Ct));
        await Link(unrelated);
        Assert.StartsWith("Stale", await Engine().FreshnessAsync(repo, check, Ct));
        Assert.DoesNotContain(unrelated, stored.CommitShas!);
        foreach (var sha in new[] { first, unrelated, last })
            Assert.True(await store.RemoveCommitAsync(repo, card.Id, sha, Ct));
        Assert.StartsWith("Stale", await Engine().FreshnessAsync(repo, check, Ct));
    }

    [Fact]
    public async Task DivergentLinkedCommitsFailVisiblyInsteadOfBroadeningScope()
    {
        await Write("example.cs", "class First {}\n");
        var first = await Commit();
        await Git("checkout", "--detach", baseline);
        await Write("example.cs", "class Second {}\n");
        var second = await Commit();
        await Link(first);
        await Link(second);
        var check = await Run(JobActionKind.CodeQuality, "card");
        Assert.Equal("Failed to run", check.Status);
        Assert.Contains("first-parent", check.Summary);
        Assert.Equal(0, check.AnalyzedCount);
    }

    [Theory]
    [InlineData("HEAD")]
    [InlineData("--all")]
    [InlineData("abc123")]
    public void ExplicitCommitRequiresAHexadecimalId(string revision) =>
        Assert.Throws<ArgumentException>(() => JobCheckScope.Parse(["commit", revision]));

    private async Task<string> Commit()
    {
        await Git("add", ".");
        await Git("-c", "core.hooksPath=", "commit", "-m", "fixture");
        return await Git("rev-parse", "HEAD");
    }

    private async Task Link(string sha) => Assert.NotNull(await store.AddCommitAsync(repo, card.Id, sha,
        "Fixture", "Selected change", DateTime.UtcNow, new([], 0), Ct));
}
