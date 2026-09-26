using Xunit;

namespace Tests.VcaRegression;

/// <summary>
/// "Check commit message for: a, b, c" defers at pre-commit (there is no message yet) and runs at
/// commit-msg: whole-word, case-insensitive, literal phrases, punctuation-bearing terms included.
/// </summary>
public sealed class CommitMessageWordsRuleTests
{
    private const string WordsRule = "Check commit message for: wip, temporary, do not merge";
    private const string PunctuationRule = "Check commit message for: TODO:, [skip], C++";

    [Fact]
    public async Task PreCommit_DefersUntilTheMessageExists()
    {
        await using var repo = await VcaRegressionRepository.CreateAsync("commit-message-words/forbidden-words");
        var run = await repo.RunPreCommitAndCrossCheckAsync();

        Assert.Equal(0, run.ExitCode);
        Assert.Contains($"[DEFERRED] {WordsRule}", run.Transcript);
        Assert.Contains("evaluated by a later Git hook", run.Transcript);
    }

    [Theory]
    [InlineData("WIP: add app\n", "wip")]
    [InlineData("Fix wip.\n", "wip")]
    [InlineData("Do Not Merge yet\n", "do not merge")]
    [InlineData("Temporary fix for the build\n", "temporary")]
    [InlineData("wip temporary do not merge\n", "wip, temporary, do not merge")]
    public async Task ForbiddenWordInMessage_BlocksAtCommitMsg(string message, string reported)
    {
        await using var repo = await VcaRegressionRepository.CreateAsync("commit-message-words/forbidden-words");
        var run = await repo.RunCommitMessageAsync(message);

        Assert.True(run.ExitCode == 1, run.Transcript);
        Assert.Contains($"[STOP] {WordsRule}", run.Transcript);
        Assert.Contains($"Commit message contains forbidden words: {reported}", run.Transcript);
    }

    [Theory]
    [InlineData("Add app\n")]
    [InlineData("Replace wiper blades\n")]
    [InlineData("Temporarily quiet the logger\n")]
    [InlineData("Do not merge_conflicts blindly\n")]
    public async Task WordsThatOnlyContainAForbiddenTerm_Pass(string message)
    {
        await using var repo = await VcaRegressionRepository.CreateAsync("commit-message-words/forbidden-words");
        var run = await repo.RunCommitMessageAsync(message);

        Assert.True(run.ExitCode == 0, run.Transcript);
        Assert.Contains("No VCA commit acknowledgments were required", run.Transcript);
    }

    [Theory]
    [InlineData("TODO: fix later\n", true)]
    [InlineData("TODO later\n", false)]
    [InlineData("[skip] ci\n", true)]
    [InlineData("skip ci\n", false)]
    [InlineData("Port to C++\n", true)]
    [InlineData("Port to C\n", false)]
    [InlineData("Use C++11 features\n", false)]
    public async Task PunctuationBearingTerms_MatchLiterally(string message, bool blocked)
    {
        await using var repo = await VcaRegressionRepository.CreateAsync("commit-message-words/punctuation-terms");
        var run = await repo.RunCommitMessageAsync(message);

        Assert.Equal(blocked ? 1 : 0, run.ExitCode);
        if (blocked)
        {
            Assert.Contains($"[STOP] {PunctuationRule}", run.Transcript);
        }
    }

    [Fact]
    public async Task MalformedListWithoutColon_WarnsAtBothHooksEvenAtStop()
    {
        await using var repo = await VcaRegressionRepository.CreateAsync("commit-message-words/malformed-no-colon");
        var preCommit = await repo.RunPreCommitAndCrossCheckAsync();
        Assert.Equal(0, preCommit.ExitCode);
        Assert.Contains("[WARN] Check commit message for", preCommit.Transcript);
        Assert.Contains("UNSUPPORTED: invalid commit-message word list", preCommit.Transcript);

        var commitMessage = await repo.RunCommitMessageAsync("wip\n");
        Assert.Equal(0, commitMessage.ExitCode);
        Assert.Contains("UNSUPPORTED: invalid commit-message word list", commitMessage.Transcript);
    }

    [Fact]
    public async Task MessageOnlyCommit_WithNothingStaged_StillChecksTheMessage()
    {
        await using var repo = await VcaRegressionRepository.CreateAsync("commit-message-words/message-only-commit");

        var blocked = await repo.RunCommitMessageAsync("WIP: amend the message only\n");
        Assert.True(blocked.ExitCode == 1, blocked.Transcript);
        Expect.StagedFiles(blocked, 0);
        Assert.Contains("Commit message contains forbidden words: wip", blocked.Transcript);

        var allowed = await repo.RunCommitMessageAsync("Amend the message only\n");
        Assert.True(allowed.ExitCode == 0, allowed.Transcript);
    }

    [Fact]
    public async Task NestedRule_DoesNotApplyWhenNothingInItsSubtreeIsStaged()
    {
        await using var repo = await VcaRegressionRepository.CreateAsync("commit-message-words/nested-scope");
        var preCommit = await repo.RunPreCommitAndCrossCheckAsync();
        Expect.NothingApplied(preCommit);

        var commitMessage = await repo.RunCommitMessageAsync("WIP: outside the nested policy\n");
        Assert.True(commitMessage.ExitCode == 0, commitMessage.Transcript);
    }

    [Theory]
    [InlineData("WARN")]
    [InlineData("COMMIT")]
    [InlineData("STOP")]
    public Task Violation_HonorsEnforcementLevel(string level) =>
        EnforcementLevels.AssertCommitMessageViolationHonorsLevelAsync(
            "commit-message-words/forbidden-words",
            level,
            WordsRule,
            "WIP: add app\n");
}
