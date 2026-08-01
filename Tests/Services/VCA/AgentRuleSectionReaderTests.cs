using VibeRails.Services.VCA;
using Xunit;

namespace Tests.Services.VCA;

public class AgentRuleSectionReaderTests
{
    [Fact]
    public void Read_SkipsFencedCodeBlocksEvenInsideARulesSection()
    {
        var rules = AgentRuleSectionReader.Read("""
            ## Vibe Rails Rules
            - Log all file changes (WARN)

            ```markdown
            - Require test coverage minimum 80% (STOP)
            ```

            - Package file changes (COMMIT)
            """);

        Assert.Equal(2, rules.Count);
        Assert.DoesNotContain(rules, rule => rule.RuleText.Contains("coverage", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Read_SkipsTildeFencedBlocks()
    {
        var rules = AgentRuleSectionReader.Read("""
            ## Vibe Rails Rules
            ~~~
            - Package file changes (STOP)
            ~~~
            - Log all file changes (WARN)
            """);

        Assert.Single(rules);
        Assert.Equal("Log all file changes", rules[0].RuleText);
    }

    [Fact]
    public void Read_StopsAtTheNextHeading()
    {
        var rules = AgentRuleSectionReader.Read("""
            ## Vibe Rails Rules
            - Log all file changes (WARN)

            ## Files
            - Package file changes (STOP)
            """);

        Assert.Single(rules);
        Assert.Equal("Log all file changes", rules[0].RuleText);
    }

    [Fact]
    public void Read_IgnoresBulletsBeforeAnyRulesHeading()
    {
        var rules = AgentRuleSectionReader.Read("""
            # Repository Guidelines
            - Cyclomatic complexity < 20 (STOP)
            """);

        Assert.Empty(rules);
    }

    [Fact]
    public void Read_IgnoresAGenericRulesHeading()
    {
        // "## Rules" is too common in ordinary documentation to be treated as policy, and the
        // old AGENTS.md example told people to write exactly that.
        var rules = AgentRuleSectionReader.Read("""
            ## Rules
            - Cyclomatic complexity < 20 (STOP)
            """);

        Assert.Empty(rules);
    }

    [Fact]
    public void Read_AcceptsTheLegacyVibeControlHeading()
    {
        var rules = AgentRuleSectionReader.Read("""
            ## Vibe Control Rules
            - Log all file changes (WARN)
            """);

        Assert.Single(rules);
    }

    [Theory]
    [InlineData("- [STOP] Log all file changes", "Log all file changes", "STOP")]
    [InlineData("- Log all file changes (STOP)", "Log all file changes", "STOP")]
    [InlineData("- Log all file changes", "Log all file changes", "WARN")]
    [InlineData("-   Log all file changes  ", "Log all file changes", "WARN")]
    [InlineData("- [disabled] Cyclomatic complexity disabled", "Cyclomatic complexity disabled", "DISABLED")]
    public void Read_AcceptsEveryDocumentedRuleForm(string line, string expectedText, string expectedEnforcement)
    {
        var rules = AgentRuleSectionReader.Read($"## Vibe Rails Rules\n{line}\n");

        var rule = Assert.Single(rules);
        Assert.Equal(expectedText, rule.RuleText);
        Assert.Equal(expectedEnforcement, rule.Enforcement);
    }

    [Fact]
    public void Read_ResumesAtASecondRulesSection()
    {
        var rules = AgentRuleSectionReader.Read("""
            ## Vibe Rails Rules
            - Log all file changes (WARN)

            ## Notes
            - not a rule (STOP)

            ## Vibe Rails Rules
            - Package file changes (COMMIT)
            """);

        Assert.Equal(2, rules.Count);
        Assert.DoesNotContain(rules, rule => rule.RuleText == "not a rule");
    }

    [Fact]
    public void Read_HandlesCrlfAndEmptyContent()
    {
        Assert.Empty(AgentRuleSectionReader.Read(null));
        Assert.Empty(AgentRuleSectionReader.Read(""));

        var rules = AgentRuleSectionReader.Read("## Vibe Rails Rules\r\n- Log all file changes (WARN)\r\n");
        Assert.Single(rules);
        Assert.Equal("Log all file changes", rules[0].RuleText);
    }

    [Fact]
    public void Read_TreatsTheRealAgentsMdDocumentationPatternAsDocumentation()
    {
        // Condensed from this repository's own AGENTS.md, which is what enforced three phantom
        // rules — including an unbypassable STOP for a coverage report the hook cannot produce.
        var rules = AgentRuleSectionReader.Read("""
            **Rule Format**:
            ```markdown
            # Agent Instructions

            ## Rules
            - Cyclomatic complexity < 20 (COMMIT)
            - Require test coverage minimum 80% (STOP)
            - Log all file changes (WARN)
            ```

            ## Vibe Control Rules
            - Log all file changes (WARN)
            """);

        var rule = Assert.Single(rules);
        Assert.Equal("Log all file changes", rule.RuleText);
        Assert.Equal("WARN", rule.Enforcement);
    }
}
