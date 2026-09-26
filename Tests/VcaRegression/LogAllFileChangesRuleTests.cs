using Xunit;

namespace Tests.VcaRegression;

public sealed class LogAllFileChangesRuleTests
{
    private const string RuleText = "Log all file changes";

    [Fact]
    public async Task UndocumentedStagedFile_Violates()
    {
        await using var repo = await VcaRegressionRepository.CreateAsync("log-all-file-changes/undocumented");
        var run = await repo.RunPreCommitAndCrossCheckAsync();

        Expect.Blocked(run, RuleText);
        Assert.Contains("1 file(s) not documented in vc.rules.md Files section: src/app.cs", run.Transcript);
    }

    [Fact]
    public async Task DocumentedStagedFile_Passes()
    {
        await using var repo = await VcaRegressionRepository.CreateAsync("log-all-file-changes/documented");
        var run = await repo.RunPreCommitAndCrossCheckAsync();

        Expect.Passed(run, applicableRules: 1);
        Expect.StagedFiles(run, 2);
    }

    [Fact]
    public async Task DeclaringRulesFile_NeverNeedsItsOwnEntry()
    {
        await using var repo = await VcaRegressionRepository.CreateAsync("log-all-file-changes/declaring-file-only");
        var run = await repo.RunPreCommitAndCrossCheckAsync();

        Expect.Passed(run, applicableRules: 1);
    }

    [Fact]
    public async Task FilesSection_AcceptsLinkBacktickAndDescribedEntries()
    {
        await using var repo = await VcaRegressionRepository.CreateAsync("log-all-file-changes/files-entry-forms");
        var run = await repo.RunPreCommitAndCrossCheckAsync();

        Expect.Passed(run, applicableRules: 1);
        Expect.StagedFiles(run, 4);
    }

    [Fact]
    public async Task UndocumentedDeletion_Violates()
    {
        await using var repo = await VcaRegressionRepository.CreateAsync("log-all-file-changes/deleted-undocumented");
        var run = await repo.RunPreCommitAndCrossCheckAsync();

        Expect.Blocked(run, RuleText);
        Assert.Contains("src/old.cs", run.Transcript);
    }

    [Fact]
    public async Task NestedFilesEntries_ResolveRelativeToTheNestedRulesFile()
    {
        await using var repo = await VcaRegressionRepository.CreateAsync("log-all-file-changes/nested-documented-relative");
        var run = await repo.RunPreCommitAndCrossCheckAsync();

        Expect.Passed(run, applicableRules: 1);
    }

    [Fact]
    public async Task ParentPolicy_StillRequiresAnEntryForANestedRulesFile()
    {
        await using var repo = await VcaRegressionRepository.CreateAsync("log-all-file-changes/nested-policy-needs-parent-entry");
        var run = await repo.RunPreCommitAndCrossCheckAsync();

        Expect.Blocked(run, RuleText);
        Assert.Contains("1 file(s) not documented in vc.rules.md Files section: nested/vc.rules.md", run.Transcript);
    }

    [Theory]
    [InlineData("WARN")]
    [InlineData("COMMIT")]
    [InlineData("STOP")]
    public Task Violation_HonorsEnforcementLevel(string level) =>
        EnforcementLevels.AssertPreCommitViolationHonorsLevelAsync("log-all-file-changes/undocumented", level, RuleText);

    [Fact]
    public async Task CommitLevel_TokenNamesTheSourceFileAndRuleSlug()
    {
        await using var repo = await VcaRegressionRepository.CreateAsync("log-all-file-changes/undocumented", "COMMIT");
        var run = await repo.RunPreCommitAndCrossCheckAsync();

        Assert.Equal(["[VCA:vc.rules.md:log-all-file-changes]"], run.AcknowledgmentTokens);
    }
}
