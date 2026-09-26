using Xunit;

namespace Tests.VcaRegression;

/// <summary>
/// File Lock('path') protects one exact Git path, relative to the declaring vc.rules.md, against
/// add, modify, delete and either side of a rename. A lock that cannot resolve never blocks.
/// </summary>
public sealed class FileLockRuleTests
{
    private const string RuleText = "File Lock('src/app.cs')";

    [Theory]
    [InlineData("path-locks/file-lock/modify-locked", "src/app.cs (modified)")]
    [InlineData("path-locks/file-lock/delete-locked", "src/app.cs (deleted)")]
    [InlineData("path-locks/file-lock/rename-locked-away", "src/app.cs -> src/renamed.cs (renamed)")]
    [InlineData("path-locks/file-lock/add-at-locked-path", "src/app.cs (added)")]
    public async Task AnyChangeToTheLockedPath_Violates(string scenario, string detail)
    {
        await using var repo = await VcaRegressionRepository.CreateAsync(scenario);
        var run = await repo.RunPreCommitAndCrossCheckAsync();

        Expect.Blocked(run, RuleText);
        Assert.Contains("Locked file 'src/app.cs' has 1 change(s)", run.Transcript);
        Assert.Contains(detail, run.Transcript);
    }

    [Fact]
    public async Task ChangeToAnotherFile_Passes()
    {
        await using var repo = await VcaRegressionRepository.CreateAsync("path-locks/file-lock/other-file-changed");
        var run = await repo.RunPreCommitAndCrossCheckAsync();

        Expect.Passed(run, applicableRules: 1);
    }

    [Fact]
    public async Task SimilarNames_AreNotTheLockedPath()
    {
        await using var repo = await VcaRegressionRepository.CreateAsync("path-locks/file-lock/similar-names-not-locked");
        var run = await repo.RunPreCommitAndCrossCheckAsync();

        Expect.Passed(run, applicableRules: 1);
        Expect.StagedFiles(run, 3);
    }

    [Fact]
    public async Task NestedLock_ResolvesRelativeToTheNestedRulesFile()
    {
        await using var repo = await VcaRegressionRepository.CreateAsync("path-locks/file-lock/nested-relative-inside");
        var run = await repo.RunPreCommitAndCrossCheckAsync();

        Expect.Blocked(run, "File Lock('app.cs')");
        Assert.Contains("Locked file 'nested/app.cs'", run.Transcript);
    }

    [Fact]
    public async Task NestedLock_DoesNotReachASameNamedFileOutsideItsDirectory()
    {
        await using var repo = await VcaRegressionRepository.CreateAsync("path-locks/file-lock/nested-relative-outside");
        var run = await repo.RunPreCommitAndCrossCheckAsync();

        Expect.NothingApplied(run);
    }

    [Theory]
    [InlineData("path-locks/file-lock/escaping-path", "File Lock('../secret.txt')", "the lock path may not escape the declaring vc.rules.md directory")]
    [InlineData("path-locks/file-lock/malformed-unquoted", "File Lock(src/app.cs)", "path locks must use File Lock('path/to/file')")]
    [InlineData("path-locks/file-lock/absolute-path", "File Lock('/etc/passwd')", "the lock path must be relative to the declaring vc.rules.md")]
    public async Task UnresolvableLock_WarnsInsteadOfBlockingEvenAtStop(string scenario, string ruleText, string reason)
    {
        await using var repo = await VcaRegressionRepository.CreateAsync(scenario);
        var run = await repo.RunPreCommitAndCrossCheckAsync();

        Expect.Warned(run, ruleText);
        Assert.Contains($"UNSUPPORTED: {reason}", run.Transcript);
        Assert.DoesNotContain("[block] Commit blocked", run.Transcript);
    }

    [Theory]
    [InlineData("WARN")]
    [InlineData("COMMIT")]
    [InlineData("STOP")]
    public Task Violation_HonorsEnforcementLevel(string level) =>
        EnforcementLevels.AssertPreCommitViolationHonorsLevelAsync("path-locks/file-lock/modify-locked", level, RuleText);
}
