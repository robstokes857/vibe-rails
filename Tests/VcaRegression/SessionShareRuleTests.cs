using Tests.Services.VCA;
using VibeRails.Services.VCA;
using Xunit;

namespace Tests.VcaRegression;

public sealed class SessionShareRuleTests
{
    [Theory]
    [InlineData("WARN")]
    [InlineData("COMMIT")]
    [InlineData("STOP")]
    public Task MissingLinkHonorsEnforcementLevel(string level) =>
        EnforcementLevels.AssertCommitMessageViolationHonorsLevelAsync(
            "session-share/basic", level, SessionShareCommitRule.Name, "Fix bug\n");

    [Fact]
    public async Task PreCommitAndMcpDeferWithInstructions()
    {
        await using var repo = await VcaRegressionRepository.CreateAsync("session-share/basic");
        var run = await repo.RunPreCommitAndCrossCheckAsync();
        Assert.Equal(0, run.ExitCode);
        Assert.Contains("[DEFERRED] " + SessionShareCommitRule.Name, run.Transcript);
        Assert.Contains("create_session_share_link", run.Transcript);
        Assert.Contains("vibe-share:", run.Transcript);
    }

    [Theory]
    [InlineData("Fix bug\n\nvibe-share:{0}\n")]
    [InlineData("Fix bug\n\nReplay: [session]({0}).\n")]
    [InlineData("Fix bug\n\nvibe-share:invalid\nvibe-share:{0}\nvibe-share:{0}\n")]
    [InlineData("Fix bug\n\nCo-authored-by: Agent <agent@example.invalid>\nvibe-share:{0}\n")]
    public async Task RealHookAcceptsLinkIncludingAfterTrailerCleanup(string template)
    {
        await using var repo = await VcaRegressionRepository.CreateAsync("session-share/basic");
        var run = await repo.RunCommitMessageAsync(string.Format(template, SessionShareCommitRuleTests.Url));
        Assert.True(run.ExitCode == 0, run.Transcript);
    }

    [Fact]
    public async Task CommentedLinkDoesNotCount()
    {
        await using var repo = await VcaRegressionRepository.CreateAsync("session-share/basic");
        var run = await repo.RunCommitMessageAsync("Fix bug\n\n# vibe-share:" + SessionShareCommitRuleTests.Url);
        Assert.Equal(1, run.ExitCode);
    }

    [Fact]
    public async Task NestedRuleDoesNotApplyToChangesOutsideItsScope()
    {
        await using var repo = await VcaRegressionRepository.CreateAsync("session-share/nested-scope");
        Expect.NothingApplied(await repo.RunPreCommitAndCrossCheckAsync());
        var commit = await repo.RunCommitMessageAsync("Change outside nested rule\n");
        Assert.Equal(0, commit.ExitCode);
    }

    [Fact]
    public async Task MessageOnlyCommitStillRequiresLink()
    {
        await using var repo = await VcaRegressionRepository.CreateAsync("session-share/basic");
        await repo.GitAsync("commit", "-qm", "baseline");
        var blocked = await repo.RunCommitMessageAsync("Amend message\n");
        Assert.Equal(1, blocked.ExitCode);
        Expect.StagedFiles(blocked, 0);
        var passed = await repo.RunCommitMessageAsync("Amend message\n\nvibe-share:" + SessionShareCommitRuleTests.Url);
        Assert.Equal(0, passed.ExitCode);
    }
}
