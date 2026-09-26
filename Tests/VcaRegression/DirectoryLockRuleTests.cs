using Xunit;

namespace Tests.VcaRegression;

/// <summary>
/// Directory Lock('dir') protects the directory and every descendant against add, modify, delete
/// and renames in either direction. Matching respects Git path boundaries, the declaring
/// vc.rules.md is exempt from its own lock, and other rules files inside the directory are not.
/// </summary>
public sealed class DirectoryLockRuleTests
{
    private const string RuleText = "Directory Lock('locked')";

    [Theory]
    [InlineData("path-locks/directory-lock/modify-inside", "locked/a.txt (modified)")]
    [InlineData("path-locks/directory-lock/modify-deep-inside", "locked/deep/b.txt (modified)")]
    [InlineData("path-locks/directory-lock/add-inside", "locked/new.txt (added)")]
    [InlineData("path-locks/directory-lock/delete-inside", "locked/a.txt (deleted)")]
    [InlineData("path-locks/directory-lock/rename-out", "locked/a.txt -> outside/a.txt (renamed)")]
    [InlineData("path-locks/directory-lock/rename-in", "outside/c.txt -> locked/c.txt (renamed)")]
    [InlineData("path-locks/directory-lock/nested-rules-file-inside-is-protected", "locked/vc.rules.md (added)")]
    public async Task AnyChangeUnderTheLockedDirectory_Violates(string scenario, string detail)
    {
        await using var repo = await VcaRegressionRepository.CreateAsync(scenario);
        var run = await repo.RunPreCommitAndCrossCheckAsync();

        Expect.Blocked(run, RuleText);
        Assert.Contains("Locked directory 'locked' has 1 change(s)", run.Transcript);
        Assert.Contains(detail, run.Transcript);
    }

    [Fact]
    public async Task TrailingSlash_LocksTheSameDirectory()
    {
        await using var repo = await VcaRegressionRepository.CreateAsync("path-locks/directory-lock/trailing-slash");
        var run = await repo.RunPreCommitAndCrossCheckAsync();

        Expect.Blocked(run, "Directory Lock('locked/')");
        Assert.Contains("Locked directory 'locked' has 1 change(s): locked/a.txt (modified)", run.Transcript);
    }

    [Fact]
    public async Task ChangeOutsideTheDirectory_Passes()
    {
        await using var repo = await VcaRegressionRepository.CreateAsync("path-locks/directory-lock/outside-change");
        var run = await repo.RunPreCommitAndCrossCheckAsync();

        Expect.Passed(run, applicableRules: 1);
    }

    [Fact]
    public async Task PrefixSiblings_AreNotInsideTheDirectory()
    {
        await using var repo = await VcaRegressionRepository.CreateAsync("path-locks/directory-lock/prefix-sibling");
        var run = await repo.RunPreCommitAndCrossCheckAsync();

        Expect.Passed(run, applicableRules: 1);
        Expect.StagedFiles(run, 2);
    }

    [Fact]
    public async Task DotLock_ExemptsTheDeclaringRulesFile()
    {
        await using var repo = await VcaRegressionRepository.CreateAsync("path-locks/directory-lock/dot-excludes-declaring-file");
        var run = await repo.RunPreCommitAndCrossCheckAsync();

        Expect.Passed(run, applicableRules: 1);
    }

    [Fact]
    public async Task DotLock_ProtectsEverythingElseInTheDeclaringDirectory()
    {
        await using var repo = await VcaRegressionRepository.CreateAsync("path-locks/directory-lock/dot-locks-siblings");
        var run = await repo.RunPreCommitAndCrossCheckAsync();

        Expect.Blocked(run, "Directory Lock('.')");
        Assert.Contains("policy/notes.txt (modified)", run.Transcript);
    }

    [Fact]
    public async Task EscapingLock_WarnsInsteadOfBlockingEvenAtStop()
    {
        await using var repo = await VcaRegressionRepository.CreateAsync("path-locks/directory-lock/escaping-path");
        var run = await repo.RunPreCommitAndCrossCheckAsync();

        Expect.Warned(run, "Directory Lock('..')");
        Assert.Contains("UNSUPPORTED: the lock path may not escape the declaring vc.rules.md directory", run.Transcript);
    }

    [Theory]
    [InlineData("WARN")]
    [InlineData("COMMIT")]
    [InlineData("STOP")]
    public Task Violation_HonorsEnforcementLevel(string level) =>
        EnforcementLevels.AssertPreCommitViolationHonorsLevelAsync("path-locks/directory-lock/modify-inside", level, RuleText);
}
