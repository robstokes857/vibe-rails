using VibeRails.DTOs;
using VibeRails.Services.Board;
using VibeRails.Services.GitPreflight;
using VibeRails.Services.VCA.Hooks;
using Xunit;

namespace Tests.Services.Board;

/// <summary>
/// VIBE-338: commits a card's own VibeRails session made are linked when they are made, and a
/// card-scoped Code quality check fails naming any that ended up unlinked. Real temporary repository.
/// </summary>
public sealed partial class BoardChecksTests
{
    private async Task<string> CommitAs(string message)
    {
        await Git("add", ".");
        await Git("-c", "core.hooksPath=", "commit", "-m", message);
        return await Git("rev-parse", "HEAD");
    }

    private async Task<string> LinkSessionAsync()
    {
        var session = Guid.NewGuid().ToString();
        Assert.NotNull(await store.LinkSessionAsync(repo, card.Id, session, null, "base:claude", "claude", "Worker", "work", Ct));
        return session;
    }

    [Fact]
    public async Task SessionCommitIsLinkedWhenMadeAndAForgottenOneFailsTheCheckByName()
    {
        var session = await LinkSessionAsync();
        await Write("first.cs", "class First { int A() => 1; }\n");
        var first = await CommitAs("Add the first class");
        // The post-commit hook's path: recorded, captured and linked to the session's card.
        Assert.Equal([card.Id], await BoardSessionCommitLinker.RecordAsync(store, new BoardCommitService(), repo, session, first, Ct));
        Assert.Contains((await store.GetCommitsAsync(repo, card.Id, Ct)), commit => commit.Sha == first);
        Assert.NotNull(await store.GetCommitSnapshotAsync(repo, card.Id, first, Ct));

        // The second commit's link failed (or was never made): only the record exists.
        await Write("second.cs", "class Second { int B() => 2; }\n");
        var second = await CommitAs("Add the second class");
        Assert.Equal([card.Id], await store.RecordSessionCommitAsync(repo, card.Id, session, second, repo, "Add the second class", Ct));

        var check = await Run(JobActionKind.CodeQuality, "card");
        Assert.True(check.FailsAction);
        Assert.Equal("Needs review", check.Status);
        Assert.Equal(1, check.AnalyzedCount); // The linked commit is still graded.
        var named = Assert.Single(check.UnlinkedCommits!);
        Assert.Equal($"{second[..8]} \"Add the second class\"", named);
        Assert.Contains(named, check.FailureReason);
        Assert.Contains("link_board_commit", check.FailureReason);
        Assert.Contains(check.Limitations, line => line.StartsWith("1 commit made from this card's sessions is not linked", StringComparison.Ordinal));
        var saved = (await store.GetCheckAsync(repo, card.Id, check.Id, Ct))!;
        Assert.Equal(check.UnlinkedCommits, saved.UnlinkedCommits);
        var review = Assert.Single(await store.GetReviewsAsync(repo, card.Id, 0, Ct));
        Assert.Equal(("Incomplete", "Failed"), (review.Result, review.ProcessStatus));
        Assert.Contains("Unlinked commits (1)", review.Findings);
        Assert.Contains(named, review.Findings);
        Assert.Equal(check.FailureReason, review.Error);

        // VCA names them as a limitation and stays advisory.
        var vca = await Run(JobActionKind.Vca, "card");
        Assert.False(vca.FailsAction);
        Assert.Equal([named], vca.UnlinkedCommits);

        // Linking it clears the failure.
        await Link(second);
        var passed = await Run(JobActionKind.CodeQuality, "card");
        Assert.Null(passed.UnlinkedCommits);
        Assert.False(passed.FailsAction);
    }

    [Fact]
    public async Task CardWithoutLinkedCodeNamesItsSessionsCommits()
    {
        var session = await LinkSessionAsync();
        await Write("only.cs", "class Only {}\n");
        var sha = await CommitAs("Only change");
        await store.RecordSessionCommitAsync(repo, card.Id, session, sha, repo, "Only change", Ct);

        var check = await Run(JobActionKind.CodeQuality, "card");
        Assert.Equal("Needs review", check.Status);
        Assert.True(check.FailsAction);
        Assert.Contains($"{sha[..8]} \"Only change\"", check.FailureReason);
    }

    [Fact]
    public async Task APersonUnlinkingACommitIsNotAForgottenLink()
    {
        var session = await LinkSessionAsync();
        await Write("kept.cs", "class Kept {}\n");
        var kept = await CommitAs("Kept");
        await Write("wrong.cs", "class Wrong {}\n");
        var wrong = await CommitAs("Belongs to another card");
        foreach (var sha in new[] { kept, wrong })
            await BoardSessionCommitLinker.RecordAsync(store, new BoardCommitService(), repo, session, sha, Ct);
        Assert.True(await store.RemoveCommitAsync(repo, card.Id, wrong, Ct));

        Assert.Empty(await store.GetUnlinkedSessionCommitsAsync(repo, card.Id, Ct));
        Assert.False((await Run(JobActionKind.CodeQuality, "card")).FailsAction);
    }

    [Fact]
    public async Task OrdinaryCommitsAndOtherRepositoriesAreNeverRecorded()
    {
        var session = await LinkSessionAsync();
        await Write("x.cs", "class X {}\n");
        var sha = await CommitAs("Unrelated session");
        // A session VibeRails does not know, and a known session committing in another repository.
        Assert.Empty(await BoardSessionCommitLinker.RecordAsync(store, new BoardCommitService(), repo, Guid.NewGuid().ToString(), sha, Ct));
        var other = Path.Combine(root, "other");
        Directory.CreateDirectory(other);
        Assert.Empty(await BoardSessionCommitLinker.RecordAsync(store, new BoardCommitService(), other, session, sha, Ct));
        Assert.Empty(await store.GetUnlinkedSessionCommitsAsync(repo, card.Id, Ct));
        Assert.Empty(await store.GetCommitsAsync(repo, card.Id, Ct));
    }

    [Fact]
    public async Task ReportListsEveryFileThatWasNotReviewedAndWhy()
    {
        Directory.CreateDirectory(Path.Combine(repo, "docs"));
        await Write("docs/readme.md", "# Notes\n");
        await Write("generated.cs", "class Generated {}\n");
        await Write("example.cs", "");
        await Write("added.cs", "class Added { int A() => 1; }\n");
        await Git("rm", "-q", "--cached", "example.cs");
        File.Delete(Path.Combine(repo, "example.cs"));
        var sha = await CommitAs("Mixed change");
        await Link(sha);
        await store.SaveCodeQualityExemptionAsync(repo, card.Id, "generated.cs", false,
            "Generated by the build; reviewed at its source.", BoardAuthor.Agent("Claude", "claude", null), Ct);
        var engine = new BoardCheckService(new(), new VcaRulesHookValidationService(new GitStagedSnapshotProvider()), services,
            exemptions: new CodeQualityExemptionService(services));

        var check = await engine.ExecuteAsync(repo, repo, card.Key, Guid.NewGuid().ToString(), Guid.NewGuid().ToString(),
            JobActionKind.CodeQuality, ["card"], Ct);
        var reasons = check.NotReviewed!.ToDictionary(file => file.Path, file => file.Reason);
        Assert.Equal(["docs/readme.md", "example.cs", "generated.cs"], reasons.Keys.Order());
        Assert.Equal("Not a supported source language.", reasons["docs/readme.md"]);
        Assert.Equal("Deleted; no added code to review.", reasons["example.cs"]);
        Assert.StartsWith("Exempted by Claude: Generated by the build", reasons["generated.cs"]);
        Assert.Equal(0, check.NotReviewedOmitted);

        var review = Assert.Single(await store.GetReviewsAsync(repo, card.Id, 0, Ct));
        Assert.Contains("Not checked (3 files in scope):", review.Findings);
        Assert.Contains("- docs/readme.md: Not a supported source language.", review.Findings);
        Assert.Contains("No file needs work (1 file checked).", review.Findings);
        // The card's comment is written for people (VIBE-362): the outcome with a button to the full
        // report, what was found, and what was not checked, grouped by why.
        var receipt = Assert.Single(await store.GetNotesAsync(repo, card.Id, Ct), comment => comment.Author.Kind == BoardAuthor.SystemKind);
        Assert.StartsWith($"**{check.Status}** · feedback only · [Open the full report](check:{check.Id})", receipt.Body);
        Assert.Contains("No single file needs work (1 checked).", receipt.Body);
        Assert.Contains("Not checked: 3 files (1 deleted, 1 exempted, 1 not a supported language).", receipt.Body);
        Assert.EndsWith("Scope: the card's linked commits.", receipt.Body);
        Assert.DoesNotContain("get_board_reviews", receipt.Body);
        Assert.DoesNotContain(review.Id, receipt.Body);
    }
}
