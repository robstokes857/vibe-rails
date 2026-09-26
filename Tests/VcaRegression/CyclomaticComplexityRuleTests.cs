using Xunit;

namespace Tests.VcaRegression;

/// <summary>
/// The estimator counts 1 + decision keywords in the staged content of code files. The fixture
/// files use only <c>if</c>, so estimated complexity is exactly (number of ifs + 1) and the
/// boundary can be pinned precisely: "&lt; 20" must admit 19 and reject 20.
/// </summary>
public sealed class CyclomaticComplexityRuleTests
{
    private static string Rule(int limit) => $"Cyclomatic complexity < {limit}";

    [Theory]
    [InlineData(20, 19)]
    [InlineData(35, 34)]
    [InlineData(60, 59)]
    public async Task ComplexityJustUnderTheLimit_Passes(int limit, int complexity)
    {
        await using var repo = await VcaRegressionRepository.CreateAsync($"cyclomatic-complexity/under-{limit}/complexity-{complexity}");
        var run = await repo.RunPreCommitAndCrossCheckAsync();

        Expect.Passed(run, applicableRules: 1);
    }

    [Theory]
    [InlineData(20, 20)]
    [InlineData(35, 35)]
    [InlineData(60, 60)]
    public async Task ComplexityExactlyAtTheLimit_IsNotUnderIt_Violates(int limit, int complexity)
    {
        await using var repo = await VcaRegressionRepository.CreateAsync($"cyclomatic-complexity/under-{limit}/complexity-{complexity}");
        var run = await repo.RunPreCommitAndCrossCheckAsync();

        Expect.Blocked(run, Rule(limit));
        Assert.Contains($"estimated complexity {complexity}", run.Transcript);
    }

    [Theory]
    [InlineData(20, 21)]
    [InlineData(35, 36)]
    [InlineData(60, 61)]
    public async Task ComplexityOverTheLimit_Violates(int limit, int complexity)
    {
        await using var repo = await VcaRegressionRepository.CreateAsync($"cyclomatic-complexity/under-{limit}/complexity-{complexity}");
        var run = await repo.RunPreCommitAndCrossCheckAsync();

        Expect.Blocked(run, Rule(limit));
        Assert.Contains($"src/Calc.cs' estimated complexity {complexity}", run.Transcript);
    }

    [Fact]
    public async Task NonCodeFiles_AreNotAnalyzed()
    {
        await using var repo = await VcaRegressionRepository.CreateAsync("cyclomatic-complexity/under-20/non-code-file-ignored");
        var run = await repo.RunPreCommitAndCrossCheckAsync();

        Expect.Passed(run, applicableRules: 1);
    }

    [Fact]
    public async Task DeletedCodeFile_IsNotAnalyzed()
    {
        await using var repo = await VcaRegressionRepository.CreateAsync("cyclomatic-complexity/under-20/deleted-complex-file");
        var run = await repo.RunPreCommitAndCrossCheckAsync();

        Expect.Passed(run, applicableRules: 1);
    }

    [Fact]
    public async Task UnstagedEdits_AreNotAnalyzed()
    {
        await using var repo = await VcaRegressionRepository.CreateAsync("cyclomatic-complexity/under-20/unstaged-edits-ignored");
        var run = await repo.RunPreCommitAndCrossCheckAsync();

        Expect.Passed(run, applicableRules: 1);
    }

    [Fact]
    public async Task JavaScriptFiles_AreAnalyzed()
    {
        await using var repo = await VcaRegressionRepository.CreateAsync("cyclomatic-complexity/under-20/javascript-file");
        var run = await repo.RunPreCommitAndCrossCheckAsync();

        Expect.Blocked(run, Rule(20));
        Assert.Contains("src/calc.js' estimated complexity 26", run.Transcript);
    }

    [Theory]
    [InlineData("WARN")]
    [InlineData("COMMIT")]
    [InlineData("STOP")]
    public async Task Disabled_NeverViolatesAtAnyLevel(string level)
    {
        await using var repo = await VcaRegressionRepository.CreateAsync("cyclomatic-complexity/disabled/complex-file", level);
        var run = await repo.RunPreCommitAndCrossCheckAsync();

        Expect.Passed(run, applicableRules: 1);
    }

    [Theory]
    [InlineData(20, "WARN")]
    [InlineData(20, "COMMIT")]
    [InlineData(20, "STOP")]
    [InlineData(35, "WARN")]
    [InlineData(35, "COMMIT")]
    [InlineData(35, "STOP")]
    [InlineData(60, "WARN")]
    [InlineData(60, "COMMIT")]
    [InlineData(60, "STOP")]
    public Task Violation_HonorsEnforcementLevel(int limit, string level) =>
        EnforcementLevels.AssertPreCommitViolationHonorsLevelAsync(
            $"cyclomatic-complexity/under-{limit}/complexity-{limit + 1}",
            level,
            Rule(limit));
}
