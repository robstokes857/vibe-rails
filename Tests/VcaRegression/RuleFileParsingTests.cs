using Xunit;

namespace Tests.VcaRegression;

/// <summary>
/// The rule-discovery contract, exercised through the real hook: which headings open a rules
/// section, where a section ends, that fenced examples are never policy, the three rule forms,
/// opt-out levels, deduplication, subtree scoping, and byte-level robustness (CRLF, BOM).
/// </summary>
public sealed class RuleFileParsingTests
{
    private const string LogAll = "Log all file changes";

    [Theory]
    [InlineData("rule-file-parsing/canonical-heading")]
    [InlineData("rule-file-parsing/legacy-heading")]
    [InlineData("rule-file-parsing/heading-case-insensitive")]
    public async Task RulesHeadings_OpenARulesSection(string scenario)
    {
        await using var repo = await VcaRegressionRepository.CreateAsync(scenario);
        var run = await repo.RunPreCommitAndCrossCheckAsync();

        Expect.Blocked(run, LogAll);
    }

    [Fact]
    public async Task PlainRulesHeading_IsNotARulesSection()
    {
        await using var repo = await VcaRegressionRepository.CreateAsync("rule-file-parsing/plain-rules-heading-ignored");
        var run = await repo.RunPreCommitAndCrossCheckAsync();

        Expect.NothingApplied(run);
        Assert.Contains("No VCA rules defined", run.Transcript);
    }

    [Fact]
    public async Task Section_EndsAtTheNextHeadingOfAnyLevel()
    {
        await using var repo = await VcaRegressionRepository.CreateAsync("rule-file-parsing/section-ends-at-next-heading");
        var run = await repo.RunPreCommitAndCrossCheckAsync();

        // Only "Skip test coverage" is inside the section; the STOP rules under ### and #### are prose.
        Expect.Passed(run, applicableRules: 1);
        Assert.DoesNotContain("[STOP]", run.Transcript);
    }

    [Fact]
    public async Task FencedExamples_AreNeverRules()
    {
        await using var repo = await VcaRegressionRepository.CreateAsync("rule-file-parsing/fenced-examples-ignored");
        var run = await repo.RunPreCommitAndCrossCheckAsync();

        Expect.NothingApplied(run);
        Assert.Contains("No VCA rules defined", run.Transcript);
    }

    [Fact]
    public async Task LiveRuleAfterAFence_IsStillRead()
    {
        await using var repo = await VcaRegressionRepository.CreateAsync("rule-file-parsing/fenced-example-then-live-rule");
        var run = await repo.RunPreCommitAndCrossCheckAsync();

        Expect.Warned(run, "Package file changes");
        Assert.DoesNotContain("[STOP]", run.Transcript);
        Assert.Contains("against 1 applicable rule(s)", run.Transcript);
    }

    [Theory]
    [InlineData("rule-file-parsing/bracket-form")]
    [InlineData("rule-file-parsing/lowercase-token")]
    [InlineData("rule-file-parsing/indented-list-item")]
    public async Task BracketForm_LowercaseToken_AndIndentedItems_AreRead(string scenario)
    {
        await using var repo = await VcaRegressionRepository.CreateAsync(scenario);
        var run = await repo.RunPreCommitAndCrossCheckAsync();

        Expect.Blocked(run, LogAll);
    }

    [Fact]
    public async Task BareForm_MeansWarn()
    {
        await using var repo = await VcaRegressionRepository.CreateAsync("rule-file-parsing/bare-form-is-warn");
        var run = await repo.RunPreCommitAndCrossCheckAsync();

        Expect.Warned(run, LogAll);
    }

    [Fact]
    public async Task SkipAndDisabled_AreNotEvaluated()
    {
        await using var repo = await VcaRegressionRepository.CreateAsync("rule-file-parsing/skip-and-disabled");
        var run = await repo.RunPreCommitAndCrossCheckAsync();

        Expect.NothingApplied(run);
        Assert.Contains("against 0 applicable rule(s)", run.Transcript);
    }

    [Fact]
    public async Task EveryRulesSectionInAFile_IsRead()
    {
        await using var repo = await VcaRegressionRepository.CreateAsync("rule-file-parsing/multiple-sections");
        var run = await repo.RunPreCommitAndCrossCheckAsync();

        Expect.Blocked(run, "Package file changes");
        Assert.Contains($"[STOP] {LogAll}", run.Transcript);
        Assert.Contains("against 2 applicable rule(s)", run.Transcript);
    }

    [Fact]
    public async Task IdenticalRuleLines_CollapseToOne()
    {
        await using var repo = await VcaRegressionRepository.CreateAsync("rule-file-parsing/duplicate-rule-collapsed");
        var run = await repo.RunPreCommitAndCrossCheckAsync();

        // Two identical STOP lines are one rule; the WARN line is a different rule.
        Expect.Blocked(run, LogAll);
        Assert.Contains("against 2 applicable rule(s)", run.Transcript);
    }

    [Fact]
    public async Task NestedRulesFile_AppliesToItsSubtree()
    {
        await using var repo = await VcaRegressionRepository.CreateAsync("rule-file-parsing/nested-scope-inside");
        var run = await repo.RunPreCommitAndCrossCheckAsync();

        Expect.Blocked(run, LogAll);
        Assert.Contains("nested/inner.txt", run.Transcript);
    }

    [Fact]
    public async Task NestedRulesFile_DoesNotApplyOutsideItsSubtree()
    {
        await using var repo = await VcaRegressionRepository.CreateAsync("rule-file-parsing/nested-scope-outside");
        var run = await repo.RunPreCommitAndCrossCheckAsync();

        Expect.NothingApplied(run);
        Assert.Contains("against 0 applicable rule(s)", run.Transcript);
    }

    [Fact]
    public async Task RootRulesFile_AppliesToEveryDepth()
    {
        await using var repo = await VcaRegressionRepository.CreateAsync("rule-file-parsing/parent-covers-nested");
        var run = await repo.RunPreCommitAndCrossCheckAsync();

        Expect.Blocked(run, LogAll);
        Assert.Contains("nested/deeper/inner.txt", run.Transcript);
    }

    [Fact]
    public async Task CrlfRulesFile_IsReadIncludingItsFilesSection()
    {
        await using var repo = await VcaRegressionRepository.CreateAsync("rule-file-parsing/crlf-line-endings");
        var run = await repo.RunPreCommitAndCrossCheckAsync();

        Expect.Passed(run, applicableRules: 2);
    }

    [Fact]
    public async Task Utf8BomBeforeTheFirstHeading_DoesNotHideTheRulesSection()
    {
        await using var repo = await VcaRegressionRepository.CreateAsync("rule-file-parsing/utf8-bom");
        var run = await repo.RunPreCommitAndCrossCheckAsync();

        Expect.Blocked(run, LogAll);
    }

    [Fact]
    public async Task AsteriskBullets_AreNotRuleLines_ByContract()
    {
        // The discovery contract accepts "- " list items only. This pins that so a change to the
        // reader is a deliberate one, not an accident.
        await using var repo = await VcaRegressionRepository.CreateAsync("rule-file-parsing/asterisk-bullet");
        var run = await repo.RunPreCommitAndCrossCheckAsync();

        Expect.NothingApplied(run);
    }

    [Fact]
    public async Task UnrecognizedRuleText_WarnsEvenAtStop()
    {
        await using var repo = await VcaRegressionRepository.CreateAsync("rule-file-parsing/unrecognized-rule-text");
        var run = await repo.RunPreCommitAndCrossCheckAsync();

        Expect.Warned(run, "Reject giant pull requests");
        Assert.Contains("UNRECOGNIZED: no validator matches this rule text", run.Transcript);
    }

    [Theory]
    [InlineData("rule-file-parsing/no-rules-file")]
    [InlineData("rule-file-parsing/unstaged-rules-file-ignored")]
    public async Task WithoutAnIndexedRulesFile_NothingIsEnforced(string scenario)
    {
        await using var repo = await VcaRegressionRepository.CreateAsync(scenario);
        var run = await repo.RunPreCommitAndCrossCheckAsync();

        Expect.NothingApplied(run);
        Assert.Contains("No vc.rules.md files found", run.Transcript);
    }
}
