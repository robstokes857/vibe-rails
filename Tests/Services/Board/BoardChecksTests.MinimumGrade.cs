using Moq;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services.Board;
using VibeRails.Services.GitPreflight;
using VibeRails.Services.VCA;
using VibeRails.Services.VCA.Hooks;
using Xunit;

namespace Tests.Services.Board;

/// <summary>VIBE-278: a Code quality action can fail its step below a minimum grade, and every check run
/// for a card is saved on it as a review report.</summary>
public sealed partial class BoardChecksTests
{
    // Grades F under today's scoring: the same fixture fails Git Guard's "Code quality minimum C" rule.
    private static readonly string MessySource = "public class Messy { public int Run(int a, int b) { "
        + string.Join("\n", Enumerable.Range(0, 8).Select(i => $"var dependency{i} = new Repository();"))
        + string.Join("\n", Enumerable.Range(0, 90).Select(i => $"if (a == {i} && b > 0) {{ a += b; }}"))
        + " return a; } }";

    [Theory]
    [InlineData(new[] { "card", "minimum", "C" }, "card", null, null, 'C')]
    [InlineData(new[] { "commit", "abcdef1", "minimum", "A" }, "commit", null, "abcdef1", 'A')]
    [InlineData(new[] { "working-tree" }, "working-tree", null, null, null)]
    public void ScopeVectorCarriesAnOptionalMinimumGrade(string[] arguments, string kind, string? @base, string? head, char? minimum)
    {
        var scope = JobCheckScope.Parse(JobActionKind.CodeQuality, arguments);
        Assert.Equal(new JobCheckScope(kind, @base, head, minimum), scope);
    }

    [Theory]
    [InlineData("card", "minimum", "D")]
    [InlineData("card", "minimum", "c")]
    [InlineData("card", "minimum")]
    [InlineData("minimum", "C")]
    [InlineData("card", "minimum", "C", "extra")]
    public void InvalidMinimumGradesAreRejected(params string[] arguments) =>
        Assert.Throws<ArgumentException>(() => JobCheckScope.Parse(JobActionKind.CodeQuality, arguments));

    [Fact]
    public async Task VcaActionsCannotCarryAMinimumGrade()
    {
        Assert.Throws<ArgumentException>(() => JobCheckScope.Parse(JobActionKind.Vca, ["working-tree", "minimum", "C"]));
        await Assert.ThrowsAsync<ArgumentException>(() => Run(JobActionKind.Vca, "working-tree", "minimum", "C"));
    }

    [Fact]
    public async Task BelowTheMinimumTheActionFailsWithTheGitGuardGrade_AndTheResultIsAReviewOnTheCard()
    {
        await Write("example.cs", MessySource);
        var check = await Run(JobActionKind.CodeQuality, "working-tree", "minimum", "C");

        Assert.Equal("C", check.MinimumGrade);
        Assert.Equal(BoardCheckRecord.GradeNotMet, check.MinimumGradeResult);
        Assert.True(check.FailsAction);
        Assert.Equal("Findings", check.Status);
        Assert.Contains("is below minimum C", check.GradeSummary);
        Assert.Contains("Minimum grade C not met", check.Summary);
        Assert.Contains(check.Limitations, line => line.StartsWith("Below minimum grade C this step fails.", StringComparison.Ordinal));
        // The same decision Git Guard's rule makes on these files.
        var snapshot = await new GitStagedSnapshotProvider().CaptureWorkingTreeAsync(repo, Ct);
        Assert.Equal(CodeQualityRule.EvaluateSnapshot('C', snapshot, Ct).Message, check.GradeSummary);
        var saved = (await store.GetCheckAsync(repo, card.Id, check.Id, Ct))!;
        Assert.Equal((check.MinimumGrade, check.MinimumGradeResult, check.GradeSummary), (saved.MinimumGrade, saved.MinimumGradeResult, saved.GradeSummary));

        var review = Assert.Single(await store.GetReviewsAsync(repo, card.Id, 0, Ct));
        Assert.Equal(BoardCheckService.CheckReviewPrefix + check.Id, review.Id);
        Assert.Equal(("VibeRails", "Code quality check", "Findings", "Failed"), (review.Provider, review.Reviewer, review.Result, review.ProcessStatus));
        Assert.Contains("Minimum grade C: not met, so this step failed.", review.Findings);
        Assert.Contains($"read_board_check checkId={check.Id}", review.Findings);
        Assert.Contains("minimum grade C", review.ScopeDescription);
        Assert.Null(review.SessionId);
        Assert.Null(review.RunId);
        var receipt = Assert.Single(await store.GetNotesAsync(repo, card.Id, Ct), comment => comment.Body.Contains(review.Id, StringComparison.Ordinal));
        Assert.StartsWith("Code quality check saved: Findings.", receipt.Body);
        Assert.Equal(BoardAuthor.SystemKind, receipt.Author.Kind);
    }

    [Fact]
    public async Task MeetingTheMinimumPasses_AndWithoutOneFindingsStayFeedback()
    {
        await Write("example.cs", "class Clean { public int Value => 1; }\n");
        var met = await Run(JobActionKind.CodeQuality, "working-tree", "minimum", "C");
        Assert.Equal(BoardCheckRecord.GradeMet, met.MinimumGradeResult);
        Assert.False(met.FailsAction);
        Assert.Contains("Minimum grade C met.", met.Summary);

        await Write("example.cs", MessySource);
        var advisory = await Run(JobActionKind.CodeQuality, "working-tree");
        Assert.Null(advisory.MinimumGrade);
        Assert.Null(advisory.MinimumGradeResult);
        Assert.False(advisory.FailsAction);

        var reviews = await store.GetReviewsAsync(repo, card.Id, 0, Ct);
        Assert.Equal(2, reviews.Count);
        var passed = reviews.Single(r => r.Id.EndsWith(met.Id, StringComparison.Ordinal));
        Assert.Equal(("No findings reported", "Succeeded"), (passed.Result, passed.ProcessStatus));
        Assert.Contains("Minimum grade C: met.", passed.Findings);
        Assert.Contains("Minimum grade: none set.", reviews.Single(r => r.Id.EndsWith(advisory.Id, StringComparison.Ordinal)).Findings);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AMinimumWithNoLinkedCodeFails_ResearchSpikesStillSkip(bool research)
    {
        if (research)
            card = (await store.UpdateCardAsync(repo, card.Id, new(Type: BoardCardTypes.ResearchSpike), Ct))!;
        var check = await Run(JobActionKind.CodeQuality, "card", "minimum", "B");

        Assert.Equal(research ? "Skipped/not applicable" : "Needs review", check.Status);
        Assert.Equal(!research, check.FailsAction);
        Assert.Equal(research ? null : BoardCheckRecord.GradeNotChecked, check.MinimumGradeResult);
        var review = Assert.Single(await store.GetReviewsAsync(repo, card.Id, 0, Ct));
        Assert.Equal(research ? "No findings reported" : "Incomplete", review.Result);
        if (!research) Assert.Contains("Minimum grade B: not checked, so this step failed. No code is attached", review.Findings);
    }

    [Fact]
    public async Task ReportIgnoresDoNotExemptCodeFromTheMinimum_ExplicitExemptionsDo()
    {
        await Write("example.cs", MessySource);
        var ignores = new Mock<ICodeAnalyzerIgnoreStore>();
        ignores.Setup(x => x.GetIgnoreRulesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new CodeAnalyzerIgnoredFile("example.cs", CodeAnalyzerIgnoreMatchKind.File, null, "Noisy", DateTime.UtcNow)]);
        var exemptions = new CodeQualityExemptionService(services);
        var engine = new BoardCheckService(new(), new VcaRulesHookValidationService(new GitStagedSnapshotProvider()), services, ignores.Object, exemptions);

        var ignored = await engine.ExecuteAsync(repo, repo, card.Key, "ignored", "quality", JobActionKind.CodeQuality, ["working-tree", "minimum", "C"], Ct);
        Assert.Equal(0, ignored.AnalyzedCount);
        Assert.Equal(BoardCheckRecord.GradeNotMet, ignored.MinimumGradeResult);
        Assert.Contains(ignored.Limitations, line => line.StartsWith("1 files hidden from the report by local ignore settings still count", StringComparison.Ordinal));

        await store.SaveCodeQualityExemptionAsync(repo, card.Id, "example.cs", false, "Generated fixture", BoardAuthor.User(), Ct);
        var exempt = await engine.ExecuteAsync(repo, repo, card.Key, "exempt", "quality", JobActionKind.CodeQuality, ["working-tree", "minimum", "C"], Ct);
        Assert.Equal(BoardCheckRecord.GradeMet, exempt.MinimumGradeResult);
        Assert.False(exempt.FailsAction);
        Assert.DoesNotContain(exempt.Limitations, line => line.Contains("hidden from the report", StringComparison.Ordinal));
    }

    // Review finding R1: the snapshot records supported source over the 5 MiB text limit (or the capture
    // budget) as binary, and the rule passes binary files as no added code. A minimum must not.
    private static string OversizedSource => MessySource + "\n/*" + new string(' ', 5 * 1024 * 1024) + "*/\n";

    [Theory]
    [InlineData("working-tree")]
    [InlineData("commit")]
    [InlineData("card")]
    public async Task UnreadableChangedSourceIsNotCheckedAndFailsTheMinimum_NotAFinding(string scope)
    {
        await Write("Messy.cs", MessySource);
        var control = await Run(JobActionKind.CodeQuality, "working-tree", "minimum", "C");
        Assert.Equal(BoardCheckRecord.GradeNotMet, control.MinimumGradeResult);

        await Write("Messy.cs", OversizedSource);
        string[] args = scope switch
        {
            "working-tree" => ["working-tree", "minimum", "C"],
            "commit" => ["commit", await Commit(), "minimum", "C"],
            _ => ["card", "minimum", "C"]
        };
        if (scope == "card") await Link(await Commit());
        var check = await Run(JobActionKind.CodeQuality, args);

        Assert.Equal(0, check.AnalyzedCount);
        Assert.Equal(BoardCheckRecord.GradeNotChecked, check.MinimumGradeResult);
        Assert.True(check.FailsAction);
        Assert.Equal("Completed with limitations", check.Status);
        Assert.Contains("could not read 1 changed source file(s): Messy.cs", check.GradeSummary);
        Assert.Contains("Minimum grade C not checked", check.Summary);
        var review = await store.GetReviewAsync(repo, card.Id, BoardCheckService.CheckReviewPrefix + check.Id, Ct);
        Assert.Equal("Incomplete", review!.Result);
        Assert.Contains("Minimum grade C: not checked, so this step failed.", review.Findings);
    }

    [Fact]
    public async Task NonCodeAndExemptChangesStillSkipAndPassTheMinimum()
    {
        await Write("notes.md", "Release notes\n");
        var docs = await Run(JobActionKind.CodeQuality, "working-tree", "minimum", "A");
        Assert.Equal(BoardCheckRecord.GradeMet, docs.MinimumGradeResult);
        Assert.False(docs.FailsAction);

        await Write("Generated.cs", OversizedSource);
        await store.SaveCodeQualityExemptionAsync(repo, card.Id, "Generated.cs", false, "Generated", BoardAuthor.User(), Ct);
        var engine = new BoardCheckService(new(), new VcaRulesHookValidationService(new GitStagedSnapshotProvider()), services,
            exemptions: new CodeQualityExemptionService(services));
        var exempt = await engine.ExecuteAsync(repo, repo, card.Key, "exempt-large", "quality", JobActionKind.CodeQuality, ["working-tree", "minimum", "A"], Ct);
        Assert.Equal(BoardCheckRecord.GradeMet, exempt.MinimumGradeResult);
        Assert.False(exempt.FailsAction);
    }

    [Fact]
    public async Task CheckReportsAreReadLikeReviewsAndSavedOnce()
    {
        await Write("example.cs", MessySource);
        var check = await Run(JobActionKind.CodeQuality, "working-tree", "minimum", "C");
        var reviewId = BoardCheckService.CheckReviewPrefix + check.Id;
        var service = new BoardReviewService(store, new Mock<IBoardService>().Object);

        var read = (await service.ReadAsync(repo, card.Key, 0, Ct))!;
        Assert.Equal("Failed", Assert.Single(read.Reviews).ProcessStatus);
        Assert.Equal(reviewId, read.Latest?.Id);
        var verified = (await service.ReportAsync(repo, card.Key, reviewId, true, repo, Ct))!;
        Assert.StartsWith("Unknown — a saved Code quality check", verified.Freshness);

        // Saved once: a repeat of the same report is refused rather than rewritten.
        Assert.False(await store.SaveCheckReviewAsync(repo, BoardCheckService.CheckReport(check with { Summary = "changed" }), Ct));
        Assert.DoesNotContain("changed", (await store.GetReviewAsync(repo, card.Id, reviewId, Ct))!.Findings);
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveCheckReviewAsync(repo,
            BoardCheckService.CheckReport(check) with { SessionId = "session" }, Ct));
    }
}
