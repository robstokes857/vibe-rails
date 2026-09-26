using Xunit;

namespace Tests.VcaRegression;

/// <summary>
/// "Log file changes > N lines" counts the staged delta (numstat added + deleted), never file
/// length, and the threshold is strict: exactly N is fine, N+1 needs a Files entry.
/// </summary>
public sealed class LogFileChangesOverLinesRuleTests
{
    private const string Over5 = "Log file changes > 5 lines";
    private const string Over10 = "Log file changes > 10 lines";

    [Theory]
    [InlineData("log-file-changes-over-5/new-file-exactly-5")]
    [InlineData("log-file-changes-over-10/new-file-exactly-10")]
    [InlineData("log-file-changes-over-10/six-lines-passes-ten")]
    public async Task NewFileAtOrUnderThreshold_Passes(string scenario)
    {
        await using var repo = await VcaRegressionRepository.CreateAsync(scenario);
        var run = await repo.RunPreCommitAndCrossCheckAsync();

        Expect.Passed(run, applicableRules: 1);
    }

    [Theory]
    [InlineData("log-file-changes-over-5/new-file-6", Over5, "src/six.txt (6 staged lines changed)")]
    [InlineData("log-file-changes-over-10/new-file-11", Over10, "src/eleven.txt (11 staged lines changed)")]
    public async Task NewFileOneLineOverThreshold_Violates(string scenario, string ruleText, string detail)
    {
        await using var repo = await VcaRegressionRepository.CreateAsync(scenario);
        var run = await repo.RunPreCommitAndCrossCheckAsync();

        Expect.Blocked(run, ruleText);
        Assert.Contains(detail, run.Transcript);
    }

    [Fact]
    public async Task ModifiedLines_CountAddedPlusDeleted()
    {
        // 3 replaced lines are 3 additions + 3 deletions = 6 changed lines: over 5.
        await using var repo = await VcaRegressionRepository.CreateAsync("log-file-changes-over-5/modified-3-lines");
        var run = await repo.RunPreCommitAndCrossCheckAsync();

        Expect.Blocked(run, Over5);
        Assert.Contains("src/app.txt (6 staged lines changed)", run.Transcript);
    }

    [Fact]
    public async Task ModifiedLines_ExactlyAtThreshold_Passes()
    {
        // 5 replaced lines = 10 changed lines: not over 10.
        await using var repo = await VcaRegressionRepository.CreateAsync("log-file-changes-over-10/modified-5-lines");
        var run = await repo.RunPreCommitAndCrossCheckAsync();

        Expect.Passed(run, applicableRules: 1);
    }

    [Fact]
    public async Task ModifiedLines_OneReplacementOverThreshold_Violates()
    {
        await using var repo = await VcaRegressionRepository.CreateAsync("log-file-changes-over-10/modified-6-lines");
        var run = await repo.RunPreCommitAndCrossCheckAsync();

        Expect.Blocked(run, Over10);
        Assert.Contains("src/app.txt (12 staged lines changed)", run.Transcript);
    }

    [Fact]
    public async Task DocumentedFileOverThreshold_Passes()
    {
        await using var repo = await VcaRegressionRepository.CreateAsync("log-file-changes-over-5/documented-over-threshold");
        var run = await repo.RunPreCommitAndCrossCheckAsync();

        Expect.Passed(run, applicableRules: 1);
    }

    [Fact]
    public async Task UnstagedEdits_DoNotCount()
    {
        await using var repo = await VcaRegressionRepository.CreateAsync("log-file-changes-over-5/unstaged-edits-ignored");
        var run = await repo.RunPreCommitAndCrossCheckAsync();

        Expect.Passed(run, applicableRules: 1);
        Expect.StagedFiles(run, 1);
    }

    [Fact]
    public async Task DeletionOverThreshold_Violates()
    {
        await using var repo = await VcaRegressionRepository.CreateAsync("log-file-changes-over-5/deleted-file-over-threshold");
        var run = await repo.RunPreCommitAndCrossCheckAsync();

        Expect.Blocked(run, Over5);
        Assert.Contains("src/gone.txt (8 staged lines changed)", run.Transcript);
    }

    [Theory]
    [InlineData("STOP", 1)]
    [InlineData("WARN", 0)]
    public async Task BinaryFile_IsReportedAsUncountableAtTheDeclaredLevel(string level, int exitCode)
    {
        await using var repo = await VcaRegressionRepository.CreateAsync("log-file-changes-over-5/binary-file", level);
        var run = await repo.RunPreCommitAndCrossCheckAsync();

        Assert.Equal(exitCode, run.ExitCode);
        Assert.Contains($"[{level}] {Over5}", run.Transcript);
        Assert.Contains("UNSUPPORTED: Git could not count changed lines for assets/blob.bin", run.Transcript);
    }

    [Theory]
    [InlineData("WARN")]
    [InlineData("COMMIT")]
    [InlineData("STOP")]
    public Task Over5Violation_HonorsEnforcementLevel(string level) =>
        EnforcementLevels.AssertPreCommitViolationHonorsLevelAsync("log-file-changes-over-5/new-file-6", level, Over5);

    [Theory]
    [InlineData("WARN")]
    [InlineData("COMMIT")]
    [InlineData("STOP")]
    public Task Over10Violation_HonorsEnforcementLevel(string level) =>
        EnforcementLevels.AssertPreCommitViolationHonorsLevelAsync("log-file-changes-over-10/new-file-11", level, Over10);
}
