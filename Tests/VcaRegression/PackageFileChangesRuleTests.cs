using Xunit;

namespace Tests.VcaRegression;

public sealed class PackageFileChangesRuleTests
{
    private const string RuleText = "Package file changes";

    [Fact]
    public async Task NoPackageFileStaged_Passes()
    {
        await using var repo = await VcaRegressionRepository.CreateAsync("package-file-changes/no-package-file");
        var run = await repo.RunPreCommitAndCrossCheckAsync();

        Expect.Passed(run, applicableRules: 1);
    }

    [Theory]
    [InlineData("package-file-changes/package-json", "1 package file(s) changed: package.json")]
    [InlineData("package-file-changes/csproj", "1 package file(s) changed: src/App.csproj")]
    [InlineData("package-file-changes/many-ecosystems", "7 package file(s) changed:")]
    public async Task StagedPackageFile_Violates(string scenario, string detail)
    {
        await using var repo = await VcaRegressionRepository.CreateAsync(scenario);
        var run = await repo.RunPreCommitAndCrossCheckAsync();

        Expect.Blocked(run, RuleText);
        Assert.Contains(detail, run.Transcript);
    }

    [Fact]
    public async Task LookalikeNames_AreNotPackageFiles()
    {
        await using var repo = await VcaRegressionRepository.CreateAsync("package-file-changes/lookalike-names");
        var run = await repo.RunPreCommitAndCrossCheckAsync();

        Expect.Passed(run, applicableRules: 1);
        Expect.StagedFiles(run, 5);
    }

    [Fact]
    public async Task DeletedPackageFile_Violates()
    {
        await using var repo = await VcaRegressionRepository.CreateAsync("package-file-changes/deleted-package-file");
        var run = await repo.RunPreCommitAndCrossCheckAsync();

        Expect.Blocked(run, RuleText);
        Assert.Contains("1 package file(s) changed: package.json", run.Transcript);
    }

    [Fact]
    public async Task NestedRule_OnlySeesPackageFilesInItsSubtree()
    {
        await using var repo = await VcaRegressionRepository.CreateAsync("package-file-changes/nested-scope");
        var run = await repo.RunPreCommitAndCrossCheckAsync();

        Expect.Blocked(run, RuleText);
        Assert.Contains("1 package file(s) changed: nested/go.mod", run.Transcript);
    }

    [Theory]
    [InlineData("WARN")]
    [InlineData("COMMIT")]
    [InlineData("STOP")]
    public Task Violation_HonorsEnforcementLevel(string level) =>
        EnforcementLevels.AssertPreCommitViolationHonorsLevelAsync("package-file-changes/package-json", level, RuleText);
}
