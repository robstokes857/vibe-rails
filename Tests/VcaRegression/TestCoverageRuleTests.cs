using Xunit;

namespace Tests.VcaRegression;

/// <summary>
/// The hook has no coverage report for a staged snapshot, so a coverage rule is "recognized but
/// unevaluable": it reports UNSUPPORTED at the declared level whenever production code is staged,
/// and is not applicable when only tests or non-code are staged. The gate must never open just
/// because a production file's name happens to contain "test" or "spec".
/// </summary>
public sealed class TestCoverageRuleTests
{
    private static string Rule(int minimum) => $"Require test coverage minimum {minimum}%";

    [Theory]
    [InlineData(50)]
    [InlineData(70)]
    [InlineData(80)]
    [InlineData(100)]
    public async Task ProductionCodeStaged_IsReportedAsUnsupportedAndBlocksAtStop(int minimum)
    {
        await using var repo = await VcaRegressionRepository.CreateAsync($"test-coverage/minimum-{minimum}/production-code-staged");
        var run = await repo.RunPreCommitAndCrossCheckAsync();

        Expect.Blocked(run, Rule(minimum));
        Assert.Contains("UNSUPPORTED: the Git hook has no coverage report for the staged snapshot", run.Transcript);
    }

    [Fact]
    public async Task OnlyTestFilesStaged_IsNotApplicable()
    {
        await using var repo = await VcaRegressionRepository.CreateAsync("test-coverage/minimum-80/tests-only-staged");
        var run = await repo.RunPreCommitAndCrossCheckAsync();

        Expect.Passed(run, applicableRules: 1);
        Expect.StagedFiles(run, 4);
    }

    [Fact]
    public async Task OnlyNonCodeStaged_IsNotApplicable()
    {
        await using var repo = await VcaRegressionRepository.CreateAsync("test-coverage/minimum-80/non-code-only");
        var run = await repo.RunPreCommitAndCrossCheckAsync();

        Expect.Passed(run, applicableRules: 1);
    }

    [Fact]
    public async Task DeletedProductionCode_IsNotApplicable()
    {
        await using var repo = await VcaRegressionRepository.CreateAsync("test-coverage/minimum-80/deleted-production-code");
        var run = await repo.RunPreCommitAndCrossCheckAsync();

        Expect.Passed(run, applicableRules: 1);
    }

    [Fact]
    public async Task ProductionFilesWhoseNamesMerelyContainTestOrSpec_StillCount()
    {
        // src/Inspector.cs, src/LatestReport.cs, src/Contest.cs and src/Protest.cs are production
        // code. A substring match on "spec" / "test" classified the first two as tests, and a
        // case-blind "ends with test" classified the last two; either silently opened the gate.
        await using var repo = await VcaRegressionRepository.CreateAsync("test-coverage/minimum-80/production-file-named-like-test");
        var run = await repo.RunPreCommitAndCrossCheckAsync();

        Expect.Blocked(run, Rule(80));
        Expect.StagedFiles(run, 5);
        Assert.Contains("UNSUPPORTED: the Git hook has no coverage report", run.Transcript);
    }

    [Theory]
    [InlineData("WARN")]
    [InlineData("COMMIT")]
    [InlineData("STOP")]
    public async Task SkipTestCoverage_NeverViolatesAtAnyLevel(string level)
    {
        await using var repo = await VcaRegressionRepository.CreateAsync("test-coverage/skip/production-code-staged", level);
        var run = await repo.RunPreCommitAndCrossCheckAsync();

        Expect.Passed(run, applicableRules: 1);
    }

    [Theory]
    [InlineData(50, "WARN")]
    [InlineData(50, "COMMIT")]
    [InlineData(50, "STOP")]
    [InlineData(70, "WARN")]
    [InlineData(70, "COMMIT")]
    [InlineData(70, "STOP")]
    [InlineData(80, "WARN")]
    [InlineData(80, "COMMIT")]
    [InlineData(80, "STOP")]
    [InlineData(100, "WARN")]
    [InlineData(100, "COMMIT")]
    [InlineData(100, "STOP")]
    public Task UnsupportedFinding_HonorsEnforcementLevel(int minimum, string level) =>
        EnforcementLevels.AssertPreCommitViolationHonorsLevelAsync(
            $"test-coverage/minimum-{minimum}/production-code-staged",
            level,
            Rule(minimum));
}
