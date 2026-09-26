using Xunit;

namespace Tests.VcaRegression;

/// <summary>Transcript-level outcomes. The strings are what Git Guard prints; the exit code is what Git obeys.</summary>
internal static class Expect
{
    /// <summary>A STOP violation: exit 1, the rule reported at STOP, and the block banner.</summary>
    public static void Blocked(HookRun run, string ruleText)
    {
        Assert.True(run.ExitCode == 1, $"expected exit 1, got {run.ExitCode}:\n{run.Transcript}");
        Assert.Contains($"[STOP] {ruleText}", run.Transcript);
        Assert.Contains("[block] Commit blocked", run.Transcript);
    }

    /// <summary>A WARN violation: reported, commit allowed.</summary>
    public static void Warned(HookRun run, string ruleText)
    {
        Assert.True(run.ExitCode == 0, $"expected exit 0, got {run.ExitCode}:\n{run.Transcript}");
        Assert.Contains($"[WARN] {ruleText}", run.Transcript);
    }

    /// <summary>A COMMIT violation at pre-commit: allowed for now, but an acknowledgment token was demanded.</summary>
    public static void AcknowledgmentRequired(HookRun run, string ruleText)
    {
        Assert.True(run.ExitCode == 0, $"expected exit 0, got {run.ExitCode}:\n{run.Transcript}");
        Assert.Contains($"[COMMIT] {ruleText}", run.Transcript);
        Assert.NotEmpty(run.AcknowledgmentTokens);
    }

    /// <summary>A clean pass with <paramref name="applicableRules"/> rule(s) actually evaluated.</summary>
    public static void Passed(HookRun run, int applicableRules)
    {
        Assert.True(run.ExitCode == 0, $"expected exit 0, got {run.ExitCode}:\n{run.Transcript}");
        Assert.Contains("PASS: All VCA rules satisfied", run.Transcript);
        Assert.Contains($"against {applicableRules} applicable rule(s)", run.Transcript);
    }

    /// <summary>Exit 0 because no rule applied: none declared, none in scope, or no rules file at all.</summary>
    public static void NothingApplied(HookRun run)
    {
        Assert.True(run.ExitCode == 0, $"expected exit 0, got {run.ExitCode}:\n{run.Transcript}");
        Assert.True(
            run.Transcript.Contains("0 applicable rule(s)", StringComparison.Ordinal)
                || run.Transcript.Contains("No VCA rules defined", StringComparison.Ordinal)
                || run.Transcript.Contains("No vc.rules.md files found", StringComparison.Ordinal),
            $"expected no applicable rules:\n{run.Transcript}");
    }

    public static void StagedFiles(HookRun run, int count) =>
        Assert.Contains($"Validated {count} staged file(s)", run.Transcript);
}

/// <summary>
/// The enforcement-level contract every rule must honor: WARN reports and continues, COMMIT
/// demands a <c>[VCA:source:slug] Reason: …</c> line in the final commit message, STOP blocks
/// and cannot be acknowledged. Each helper drives the real pre-commit and commit-msg hooks.
/// </summary>
internal static class EnforcementLevels
{
    private const string CleanMessage = "Regression commit\n";

    /// <summary>For rules that violate at pre-commit time (everything except the commit-message rule).</summary>
    public static async Task AssertPreCommitViolationHonorsLevelAsync(string scenario, string level, string ruleText)
    {
        await using var repo = await VcaRegressionRepository.CreateAsync(scenario, level);
        var preCommit = await repo.RunPreCommitAndCrossCheckAsync();

        switch (level)
        {
            case "WARN":
            {
                Expect.Warned(preCommit, ruleText);
                Assert.Empty(preCommit.AcknowledgmentTokens);
                var commitMessage = await repo.RunCommitMessageAsync(CleanMessage);
                Assert.True(commitMessage.ExitCode == 0, commitMessage.Transcript);
                Assert.Contains("No VCA commit acknowledgments were required", commitMessage.Transcript);
                break;
            }

            case "COMMIT":
            {
                Expect.AcknowledgmentRequired(preCommit, ruleText);
                Assert.Contains("VCA requires commit-message acknowledgment", preCommit.Transcript);
                await AssertCommitMessageDemandsReasonAsync(repo, CleanMessage, preCommit.AcknowledgmentTokens);
                break;
            }

            case "STOP":
            {
                Expect.Blocked(preCommit, ruleText);
                Assert.Empty(preCommit.AcknowledgmentTokens);
                var commitMessage = await repo.RunCommitMessageAsync(CleanMessage);
                Assert.True(commitMessage.ExitCode == 1, commitMessage.Transcript);
                Assert.Contains("blocking validation still fails", commitMessage.Transcript);
                break;
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(level), level, "Unknown enforcement level.");
        }
    }

    /// <summary>For the commit-message rule, whose violation can only surface at commit-msg time.</summary>
    public static async Task AssertCommitMessageViolationHonorsLevelAsync(
        string scenario,
        string level,
        string ruleText,
        string forbiddenMessage)
    {
        await using var repo = await VcaRegressionRepository.CreateAsync(scenario, level);
        var preCommit = await repo.RunPreCommitAndCrossCheckAsync();
        Assert.True(preCommit.ExitCode == 0, preCommit.Transcript);
        Assert.Contains($"[DEFERRED] {ruleText}", preCommit.Transcript);

        var commitMessage = await repo.RunCommitMessageAsync(forbiddenMessage);
        var tool = await repo.ValidateWithMcpToolAsync(forbiddenMessage);
        Assert.False(tool.HasError, tool.Output);

        switch (level)
        {
            case "WARN":
                Assert.True(commitMessage.ExitCode == 0, commitMessage.Transcript);
                Assert.Contains($"[WARN] {ruleText}", commitMessage.Transcript);
                Assert.False(tool.HasStopViolation, tool.Output);
                Assert.Empty(tool.RequiredAcknowledgments);
                break;

            case "COMMIT":
                Assert.True(commitMessage.ExitCode == 1, commitMessage.Transcript);
                Assert.Contains($"[COMMIT] {ruleText}", commitMessage.Transcript);
                Assert.Contains("missing required VCA acknowledgment", commitMessage.Transcript);
                Assert.Equal(
                    commitMessage.AcknowledgmentTokens.OrderBy(token => token, StringComparer.Ordinal),
                    tool.RequiredAcknowledgments.OrderBy(token => token, StringComparer.Ordinal));
                await AssertCommitMessageDemandsReasonAsync(repo, forbiddenMessage, commitMessage.AcknowledgmentTokens);
                break;

            case "STOP":
                Assert.True(commitMessage.ExitCode == 1, commitMessage.Transcript);
                Assert.Contains($"[STOP] {ruleText}", commitMessage.Transcript);
                Assert.Contains("blocking validation still fails", commitMessage.Transcript);
                Assert.True(tool.HasStopViolation, tool.Output);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(level), level, "Unknown enforcement level.");
        }
    }

    private static async Task AssertCommitMessageDemandsReasonAsync(
        VcaRegressionRepository repo,
        string baseMessage,
        IReadOnlyList<string> tokens)
    {
        Assert.NotEmpty(tokens);
        var body = baseMessage.TrimEnd() + "\n\n";

        var missing = await repo.RunCommitMessageAsync(baseMessage);
        Assert.True(missing.ExitCode == 1, missing.Transcript);
        Assert.Contains("missing required VCA acknowledgment", missing.Transcript);

        var tokenOnly = await repo.RunCommitMessageAsync(body + string.Join('\n', tokens) + "\n");
        Assert.True(tokenOnly.ExitCode == 1, tokenOnly.Transcript);
        Assert.Contains("missing required VCA acknowledgment", tokenOnly.Transcript);

        var blankReason = await repo.RunCommitMessageAsync(
            body + string.Join('\n', tokens.Select(token => $"{token} Reason:   ")) + "\n");
        Assert.True(blankReason.ExitCode == 1, blankReason.Transcript);

        var commented = await repo.RunCommitMessageAsync(
            body + string.Join('\n', tokens.Select(token => $"# {token} Reason: git strips this comment line")) + "\n");
        Assert.True(commented.ExitCode == 1, commented.Transcript);

        var acknowledged = await repo.RunCommitMessageAsync(
            body + string.Join('\n', tokens.Select(token => $"{token} Reason: reviewed for the regression suite")) + "\n");
        Assert.True(acknowledged.ExitCode == 0, acknowledged.Transcript);
        Assert.Contains("acknowledgments found", acknowledged.Transcript);
    }
}
