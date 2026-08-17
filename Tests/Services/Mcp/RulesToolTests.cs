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
            ## Vibe Rails Rules
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
            ## Vibe Rails Rules
            - [WARN] Log all file changes (STOP)
            """;

        var rules = RulesTool.ParseRules(content, "AGENTS.md");

        Assert.Single(rules);
        Assert.Equal("Log all file changes (STOP)", rules[0].RuleText);
        Assert.Equal("WARN", rules[0].Enforcement);
    }

    [Fact]
    public void ParseRules_IgnoresRuleLinesOutsideARulesSection()
    {
        // The whole file used to be scanned, so any prose bullet that happened to end in
        // "(STOP)" became policy.
        const string content = """
            ## Rule Format
            - Require test coverage minimum 80% (STOP)

            ## Vibe Rails Rules
            - Log all file changes (WARN)

            ## Files
            - Cyclomatic complexity < 20 (STOP)
            """;

        var rules = RulesTool.ParseRules(content, "AGENTS.md");

        Assert.Single(rules);
        Assert.Equal("Log all file changes", rules[0].RuleText);
    }

    [Fact]
    public void ParseRules_IgnoresExamplesInsideFencedCodeBlocks()
    {
        // This is the AGENTS.md pattern that blocked a real commit: a fenced example of how to
        // write rules was read as three live rules, one of them an unbypassable STOP.
        const string content = """
            ## Vibe Rails Rules
            - Log all file changes (WARN)

            Write rules like this:

            ```markdown
            ## Vibe Rails Rules
            - Cyclomatic complexity < 20 (COMMIT)
            - Require test coverage minimum 80% (STOP)
            ```
            """;

        var rules = RulesTool.ParseRules(content, "AGENTS.md");

        Assert.Single(rules);
        Assert.Equal("Log all file changes", rules[0].RuleText);
        Assert.DoesNotContain(rules, rule => rule.RuleText.Contains("coverage", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ParseRules_ReadsBareBulletsAsWarn()
    {
        // AgentFileService.AddRulesAsync writes bare "- rule" lines, so the hook ignoring them
        // meant the Rules page could add a rule that never ran.
        const string content = """
            ## Vibe Rails Rules
            - Log all file changes
            """;

        var rules = RulesTool.ParseRules(content, "AGENTS.md");

        Assert.Single(rules);
        Assert.Equal("Log all file changes", rules[0].RuleText);
        Assert.Equal("WARN", rules[0].Enforcement);
    }

    [Fact]
    public async Task ValidateVcaReportAsync_UnrecognizedRule_WarnsInsteadOfBlocking()
    {
        var snapshot = CreateSnapshot(
            "AGENTS.md",
            """
            # Agent Instructions
            ## Vibe Rails Rules
            - My new rule display text (STOP)
            ## Files
            - AGENTS.md
            - src/app.cs
            """,
            "src/app.cs");

        var report = await RulesTool.ValidateVcaReportAsync(
            cancellationToken: TestContext.Current.CancellationToken,
            stagedSnapshot: snapshot);

        // A rule no validator understands describes a check that is not running. Blocking the
        // commit over it would enforce nothing while costing the user the commit.
        Assert.False(report.HasStopViolation);
        Assert.Empty(report.RequiredAcknowledgments);
        var finding = Assert.Single(report.Findings);
        Assert.Equal(VcaRuleFindingKind.Warning, finding.Kind);
        Assert.Equal("My new rule display text", finding.Rule);
        Assert.Contains("UNRECOGNIZED", finding.Reason, StringComparison.Ordinal);
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

    [Fact]
    public async Task ValidateVcaReportAsync_CountsSpacedBooleanOperatorsInComplexity()
    {
        var snapshot = CreateSnapshot(
            "AGENTS.md",
            """
            # Agent Instructions
            ## Vibe Rails Rules
            - [STOP] Cyclomatic complexity < 2
            """,
            "src/app.cs",
            "bool IsReady(bool a, bool b, bool c) => a && b || c;");

        var report = await RulesTool.ValidateVcaReportAsync(
            cancellationToken: TestContext.Current.CancellationToken,
            stagedSnapshot: snapshot);

        Assert.True(report.HasStopViolation);
        Assert.Contains(
            report.Findings,
            finding => finding.Rule.Contains("Cyclomatic complexity", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ValidateVcaReportAsync_DoesNotCountNullableAndNullConditionalSyntaxAsComplexity()
    {
        var snapshot = CreateSnapshot(
            "AGENTS.md",
            """
            # Agent Instructions
            ## Vibe Rails Rules
            - [STOP] Cyclomatic complexity < 2
            """,
            "src/app.cs",
            "string? Select(Input? input) => input?.Value ?? string.Empty;");

        var report = await RulesTool.ValidateVcaReportAsync(
            cancellationToken: TestContext.Current.CancellationToken,
            stagedSnapshot: snapshot);

        Assert.False(report.HasStopViolation);
        Assert.DoesNotContain(
            report.Findings,
            finding => finding.Rule.Contains("Cyclomatic complexity", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ValidateVcaReportAsync_FileLockBlocksTheExactModifiedFile()
    {
        var snapshot = CreatePathLockSnapshot(
            "AGENTS.md",
            """
            ## Vibe Rails Rules
            - File Lock('src/app.cs') (STOP)
            """,
            "src/app.cs",
            GitStagedChangeKind.Modified);

        var report = await RulesTool.ValidateVcaReportAsync(
            cancellationToken: TestContext.Current.CancellationToken,
            stagedSnapshot: snapshot);

        Assert.True(report.HasStopViolation);
        var finding = Assert.Single(report.Findings);
        Assert.Equal(VcaRuleFindingKind.Blocked, finding.Kind);
        Assert.Contains("Locked file 'src/app.cs'", finding.Reason);
        Assert.Contains("modified", finding.Reason);
    }

    [Theory]
    [InlineData(GitStagedChangeKind.Added, "locked/new.txt", null)]
    [InlineData(GitStagedChangeKind.Modified, "locked/existing.txt", null)]
    [InlineData(GitStagedChangeKind.Deleted, "locked/removed.txt", null)]
    [InlineData(GitStagedChangeKind.Renamed, "outside/moved.txt", "locked/moved.txt")]
    public async Task ValidateVcaReportAsync_DirectoryLockBlocksEveryGitChangeKind(
        GitStagedChangeKind changeKind,
        string changedPath,
        string? previousPath)
    {
        var snapshot = CreatePathLockSnapshot(
            "AGENTS.md",
            """
            ## Vibe Rails Rules
            - Directory Lock('locked') (STOP)
            """,
            changedPath,
            changeKind,
            previousPath);

        var report = await RulesTool.ValidateVcaReportAsync(
            cancellationToken: TestContext.Current.CancellationToken,
            stagedSnapshot: snapshot);

        Assert.True(report.HasStopViolation);
        Assert.Contains(report.Findings, finding =>
            finding.Kind == VcaRuleFindingKind.Blocked
            && finding.Reason.Contains("Locked directory 'locked'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ValidateVcaReportAsync_DirectoryLockDoesNotMatchAPrefixSibling()
    {
        var snapshot = CreatePathLockSnapshot(
            "AGENTS.md",
            """
            ## Vibe Rails Rules
            - Directory Lock('locked') (STOP)
            """,
            "locked-old/file.txt",
            GitStagedChangeKind.Modified);

        var report = await RulesTool.ValidateVcaReportAsync(
            cancellationToken: TestContext.Current.CancellationToken,
            stagedSnapshot: snapshot);

        Assert.False(report.HasStopViolation);
        Assert.Empty(report.Findings);
    }

    [Fact]
    public async Task ValidateVcaReportAsync_PathLockIsRelativeToNestedAgentDirectory()
    {
        var snapshot = CreatePathLockSnapshot(
            "nested/AGENTS.md",
            """
            ## Vibe Rails Rules
            - File Lock('config/settings.json') (STOP)
            """,
            "nested/config/settings.json",
            GitStagedChangeKind.Modified);

        var report = await RulesTool.ValidateVcaReportAsync(
            cancellationToken: TestContext.Current.CancellationToken,
            stagedSnapshot: snapshot);

        Assert.True(report.HasStopViolation);
        Assert.Contains("nested/config/settings.json", Assert.Single(report.Findings).Reason);
    }

    [Fact]
    public async Task ValidateVcaReportAsync_DirectoryLockDoesNotLockItsDeclaringAgentFile()
    {
        var snapshot = CreatePathLockSnapshot(
            "AGENTS.md",
            """
            ## Vibe Rails Rules
            - Directory Lock('.') (STOP)
            """,
            changedPath: null,
            GitStagedChangeKind.Modified);

        var report = await RulesTool.ValidateVcaReportAsync(
            cancellationToken: TestContext.Current.CancellationToken,
            stagedSnapshot: snapshot);

        Assert.False(report.HasStopViolation);
        Assert.Empty(report.Findings);
    }

    [Fact]
    public async Task ValidateVcaReportAsync_WarnPathLockReportsWithoutBlocking()
    {
        var snapshot = CreatePathLockSnapshot(
            "AGENTS.md",
            """
            ## Vibe Rails Rules
            - File Lock('src/app.cs') (WARN)
            """,
            "src/app.cs",
            GitStagedChangeKind.Deleted);

        var report = await RulesTool.ValidateVcaReportAsync(
            cancellationToken: TestContext.Current.CancellationToken,
            stagedSnapshot: snapshot);

        Assert.False(report.HasStopViolation);
        Assert.Equal(VcaRuleFindingKind.Warning, Assert.Single(report.Findings).Kind);
    }

    [Fact]
    public async Task ValidateVcaReportAsync_CommitPathLockRequiresAcknowledgment()
    {
        var snapshot = CreatePathLockSnapshot(
            "AGENTS.md",
            """
            ## Vibe Rails Rules
            - Directory Lock('locked') (COMMIT)
            """,
            "locked/app.cs",
            GitStagedChangeKind.Modified);

        var report = await RulesTool.ValidateVcaReportAsync(
            cancellationToken: TestContext.Current.CancellationToken,
            stagedSnapshot: snapshot);

        Assert.False(report.HasStopViolation);
        Assert.Single(report.RequiredAcknowledgments);
        Assert.Equal(VcaRuleFindingKind.AcknowledgmentRequired, Assert.Single(report.Findings).Kind);
    }

    [Fact]
    public async Task ValidateVcaReportAsync_EscapingPathLockWarnsWithoutBlockingAtStop()
    {
        var snapshot = CreatePathLockSnapshot(
            "nested/AGENTS.md",
            """
            ## Vibe Rails Rules
            - Directory Lock('../outside') (STOP)
            """,
            "nested/app.cs",
            GitStagedChangeKind.Modified);

        var report = await RulesTool.ValidateVcaReportAsync(
            cancellationToken: TestContext.Current.CancellationToken,
            stagedSnapshot: snapshot);

        // A lock that will not resolve has no path to compare the staged files against, so
        // blocking on it fires on every commit regardless of what changed - and at STOP that
        // wedges all work until AGENTS.md is hand-edited. The rule is still surfaced.
        Assert.False(report.HasStopViolation);
        Assert.Empty(report.RequiredAcknowledgments);
        var finding = Assert.Single(report.Findings);
        Assert.Equal(VcaRuleFindingKind.Warning, finding.Kind);
        Assert.Contains("UNSUPPORTED", finding.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidateVcaReportAsync_MalformedPathLockWarnsWithoutBlockingAtStop()
    {
        var snapshot = CreatePathLockSnapshot(
            "nested/AGENTS.md",
            """
            ## Vibe Rails Rules
            - Directory Lock() (STOP)
            """,
            "nested/app.cs",
            GitStagedChangeKind.Modified);

        var report = await RulesTool.ValidateVcaReportAsync(
            cancellationToken: TestContext.Current.CancellationToken,
            stagedSnapshot: snapshot);

        // Same reasoning as the escaping lock: unparseable means no path, and matching every
        // staged file on an empty path would violate on every commit.
        Assert.False(report.HasStopViolation);
        Assert.Empty(report.RequiredAcknowledgments);
        var finding = Assert.Single(report.Findings);
        Assert.Equal(VcaRuleFindingKind.Warning, finding.Kind);
        Assert.Contains("UNSUPPORTED", finding.Reason, StringComparison.Ordinal);
    }

    private static GitStagedSnapshot CreatePathLockSnapshot(
        string agentPath,
        string agentContent,
        string? changedPath,
        GitStagedChangeKind changeKind,
        string? previousPath = null)
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "vca-path-lock-tests"));
        var files = new List<GitStagedFileSnapshot>
        {
            new(
                agentPath,
                Path.Combine(root, agentPath.Replace('/', Path.DirectorySeparatorChar)),
                GitStagedChangeKind.Modified,
                ExistsInIndex: true,
                IsBinary: false,
                ChangedLineCount: 1,
                Content: agentContent)
        };

        if (changedPath is not null)
        {
            files.Add(new GitStagedFileSnapshot(
                changedPath,
                Path.Combine(root, changedPath.Replace('/', Path.DirectorySeparatorChar)),
                changeKind,
                ExistsInIndex: changeKind != GitStagedChangeKind.Deleted,
                IsBinary: false,
                ChangedLineCount: 1,
                Content: changeKind == GitStagedChangeKind.Deleted ? null : "changed content",
                PreviousRelativePath: previousPath));
        }

        return new GitStagedSnapshot(
            root,
            files,
            [new GitIndexTextFile(agentPath, agentContent)]);
    }

    private static GitStagedSnapshot CreateSnapshot(
        string agentPath,
        string agentContent,
        string changedPath,
        string changedContent = "staged content")
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
                    Content: changedContent)
            ],
            [new GitIndexTextFile(agentPath, agentContent)]);
    }
}
