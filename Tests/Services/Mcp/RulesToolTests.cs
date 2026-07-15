using VibeRails.Services.Mcp.Tools;
using VibeRails.Services.GitPreflight;
using VibeRails.Services.VCA.Hooks;
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

    [Fact]
    public async Task ValidateVcaReportAsync_ReturnsStructuredStopFinding()
    {
        var snapshot = CreateSnapshot(
            "AGENTS.md",
            """
            # Agent Instructions
            ## Vibe Rails Rules
            - Log all file changes (STOP)
            ## Files
            - AGENTS.md
            """,
            "src/app.cs");

        var report = await RulesTool.ValidateVcaReportAsync(
            cancellationToken: TestContext.Current.CancellationToken,
            stagedSnapshot: snapshot);

        Assert.True(report.HasStopViolation);
        Assert.Equal(2, report.StagedFileCount);
        Assert.Equal(1, report.ApplicableRuleCount);
        var finding = Assert.Single(report.Findings);
        Assert.Equal(VcaRuleFindingKind.Blocked, finding.Kind);
        Assert.Equal("STOP", finding.Enforcement);
        Assert.Equal("Log all file changes", finding.Rule);
        Assert.Equal("AGENTS.md", finding.SourcePath);
        Assert.Contains("src/app.cs", finding.Reason);
        Assert.Contains("cannot be acknowledged", finding.Guidance, StringComparison.OrdinalIgnoreCase);
        Assert.Null(finding.Acknowledgment);
    }

    [Fact]
    public async Task ValidateVcaReportAsync_ReturnsNestedCommitFindingWithAcknowledgment()
    {
        var snapshot = CreateSnapshot(
            "nested/AGENTS.md",
            """
            # Nested instructions
            ## Vibe Rails Rules
            - Package file changes (COMMIT)
            """,
            "nested/package.json");

        var report = await RulesTool.ValidateVcaReportAsync(
            cancellationToken: TestContext.Current.CancellationToken,
            stagedSnapshot: snapshot);

        Assert.False(report.HasStopViolation);
        var finding = Assert.Single(report.Findings);
        Assert.Equal(VcaRuleFindingKind.AcknowledgmentRequired, finding.Kind);
        Assert.Equal("COMMIT", finding.Enforcement);
        Assert.Equal("nested/AGENTS.md", finding.SourcePath);
        Assert.Contains("nested/package.json", finding.Reason);
        Assert.StartsWith("[VCA:nested-AGENTS.md:", finding.Acknowledgment, StringComparison.Ordinal);
        Assert.Contains(finding.Acknowledgment!, report.RequiredAcknowledgments);
    }

    private static GitStagedSnapshot CreateSnapshot(
        string agentPath,
        string agentContent,
        string changedPath)
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "vca-structured-tests"));
        return new GitStagedSnapshot(
            root,
            [
                new GitStagedFileSnapshot(
                    agentPath,
                    Path.Combine(root, agentPath.Replace('/', Path.DirectorySeparatorChar)),
                    GitStagedChangeKind.Modified,
                    ExistsInIndex: true,
                    IsBinary: false,
                    ChangedLineCount: 4,
                    Content: agentContent),
                new GitStagedFileSnapshot(
                    changedPath,
                    Path.Combine(root, changedPath.Replace('/', Path.DirectorySeparatorChar)),
                    GitStagedChangeKind.Modified,
                    ExistsInIndex: true,
                    IsBinary: false,
                    ChangedLineCount: 3,
                    Content: "staged content")
            ],
            [new GitIndexTextFile(agentPath, agentContent)]);
    }
}
