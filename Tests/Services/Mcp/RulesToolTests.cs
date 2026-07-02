using VibeRails.Services.Mcp.Tools;
using Xunit;

namespace Tests.Services.Mcp;

public class RulesToolTests
{
    [Fact]
    public void ParseRules_AcceptsBracketAndSuffixEnforcementFormats()
    {
        const string content = """
            ## Vibe Control Rules
            - Log all file changes (WARN)
            - [STOP] Package file changes
            - [disabled] Cyclomatic complexity disabled
            """;

        var rules = RulesTool.ParseRules(content, "AGENTS.md");

        Assert.Contains(rules, r => r.RuleText == "Log all file changes" && r.Enforcement == "WARN");
        Assert.Contains(rules, r => r.RuleText == "Package file changes" && r.Enforcement == "STOP");
        Assert.Contains(rules, r => r.RuleText == "Cyclomatic complexity disabled" && r.Enforcement == "DISABLED");
    }

    [Fact]
    public void ParseRules_DeduplicatesSameRuleWhenFormatsOverlap()
    {
        const string content = """
            - [COMMIT] Log all file changes
            - Log all file changes (COMMIT)
            """;

        var rules = RulesTool.ParseRules(content, "AGENTS.md");

        Assert.Single(rules);
        Assert.Equal("Log all file changes", rules[0].RuleText);
        Assert.Equal("COMMIT", rules[0].Enforcement);
    }

    [Fact]
    public void ParseRules_DoesNotTreatSuffixInsideBracketRuleAsSecondRule()
    {
        const string content = """
            - [WARN] Log all file changes (STOP)
            """;

        var rules = RulesTool.ParseRules(content, "AGENTS.md");

        Assert.Single(rules);
        Assert.Equal("Log all file changes (STOP)", rules[0].RuleText);
        Assert.Equal("WARN", rules[0].Enforcement);
    }
}
