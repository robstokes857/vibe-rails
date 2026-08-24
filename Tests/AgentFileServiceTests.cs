using VibeRails.DTOs;
using VibeRails.Services;
using VibeRails.Services.VCA;
using Xunit;

namespace Tests;

public class AgentFileServiceTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly AgentFileService _service;
    private readonly MockRulesService _mockRulesService;
    private readonly MockGitService _mockGitService;

    public AgentFileServiceTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"AgentFileServiceTests_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDirectory);

        _mockRulesService = new MockRulesService();
        _mockGitService = new MockGitService(_testDirectory);
        _service = new AgentFileService(_mockGitService, _mockRulesService);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
    }

    private async Task<string> CreateTestAgentFile(string content)
    {
        var filePath = Path.Combine(_testDirectory, "vc.rules.md");
        await File.WriteAllTextAsync(filePath, content);
        return filePath;
    }

    // ===========================================
    // GetRulesAsync Tests
    // ===========================================

    [Fact]
    public async Task GetRulesAsync_ReturnsEmptyList_WhenNoRulesExist()
    {
        var filePath = await CreateTestAgentFile(@"# Agent File Header

## Vibe Rails Rules

## Files
- some-file.cs
");

        var rules = await _service.GetRulesAsync(filePath, CancellationToken.None);

        Assert.Empty(rules);
    }

    [Fact]
    public async Task GetRulesAsync_ReturnsRules_WhenRulesExist()
    {
        var filePath = await CreateTestAgentFile(@"# Agent File Header

## Vibe Rails Rules
- Log all file changes
- Require test coverage minimum 80%

## Files
- some-file.cs
");

        var rules = await _service.GetRulesAsync(filePath, CancellationToken.None);

        Assert.Equal(2, rules.Count);
        Assert.Contains("Log all file changes", rules);
        Assert.Contains("Require test coverage minimum 80%", rules);
    }

    [Fact]
    public async Task GetRulesAsync_TrimsWhitespace_FromRules()
    {
        var filePath = await CreateTestAgentFile(@"# Header

## Vibe Rails Rules
-   Log all file changes
-    Require test coverage minimum 80%

## Files
");

        var rules = await _service.GetRulesAsync(filePath, CancellationToken.None);

        Assert.Equal(2, rules.Count);
        Assert.Equal("Log all file changes", rules[0]);
        Assert.Equal("Require test coverage minimum 80%", rules[1]);
    }

    // ===========================================
    // AddRulesAsync Tests
    // ===========================================

    [Fact]
    public async Task AddRulesAsync_AddsRulesToFile()
    {
        var filePath = await CreateTestAgentFile(@"# Agent File Header

## Vibe Rails Rules

## Files
- some-file.cs
");

        await _service.AddRulesAsync(filePath, CancellationToken.None, "Log all file changes");

        var rules = await _service.GetRulesAsync(filePath, CancellationToken.None);
        Assert.Single(rules);
        Assert.Equal("Log all file changes", rules[0]);
    }

    [Fact]
    public async Task AddRulesAsync_AddsMultipleRules()
    {
        var filePath = await CreateTestAgentFile(@"# Agent File Header

## Vibe Rails Rules

## Files
");

        await _service.AddRulesAsync(filePath, CancellationToken.None, "Log all file changes", "Require test coverage minimum 80%");

        var rules = await _service.GetRulesAsync(filePath, CancellationToken.None);
        Assert.Equal(2, rules.Count);
        Assert.Contains("Log all file changes", rules);
        Assert.Contains("Require test coverage minimum 80%", rules);
    }

    [Fact]
    public async Task AddRulesAsync_IgnoresInvalidRules()
    {
        var filePath = await CreateTestAgentFile(@"# Header
## Vibe Rails Rules
## Files
");

        await _service.AddRulesAsync(filePath, CancellationToken.None, "Not an allowed rule");

        var rules = await _service.GetRulesAsync(filePath, CancellationToken.None);
        Assert.Empty(rules);
    }

    [Fact]
    public async Task AddRulesAsync_PreservesExistingRules()
    {
        var filePath = await CreateTestAgentFile(@"# Header
## Vibe Rails Rules
- Log all file changes
## Files
");

        await _service.AddRulesAsync(filePath, CancellationToken.None, "Require test coverage minimum 80%");

        var rules = await _service.GetRulesAsync(filePath, CancellationToken.None);
        Assert.Equal(2, rules.Count);
        Assert.Contains("Log all file changes", rules);
        Assert.Contains("Require test coverage minimum 80%", rules);
    }

    [Fact]
    public async Task AddRulesAsync_PreservesExistingRulesAndWhitespaces()
    {
        var filePath = await CreateTestAgentFile(@"# Header
## Vibe Rails Rules

- Log all file changes

- Cyclomatic complexity < 60

## Files
");

        await _service.AddRulesAsync(filePath, CancellationToken.None, "Require test coverage minimum 80%");

        var rules = await _service.GetRulesAsync(filePath, CancellationToken.None);
        Assert.Equal(3, rules.Count);
        Assert.Contains("Log all file changes", rules);
        Assert.Contains("Require test coverage minimum 80%", rules);
        Assert.Contains("Cyclomatic complexity < 60", rules);
    }


    [Fact]
    public async Task AddRulesAsync_PreservesOtherSections()
    {
        var originalContent = @"# Agent File Header

## Vibe Rails Rules

## Files
- important-file.cs
- another-file.cs

## Prompts
- Custom prompt here
";
        var filePath = await CreateTestAgentFile(originalContent);

        await _service.AddRulesAsync(filePath, CancellationToken.None, "Log all file changes");

        var content = await File.ReadAllTextAsync(filePath, TestContext.Current.CancellationToken);
        Assert.Contains("## Files", content);
        Assert.Contains("important-file.cs", content);
        Assert.Contains("another-file.cs", content);
        Assert.Contains("## Prompts", content);
        Assert.Contains("Custom prompt here", content);
    }

    [Fact]
    public async Task AddRulesAsync_IgnoresFencedRuleExamplesBeforeTheLiveSection()
    {
        var filePath = await CreateTestAgentFile("""
            # Agent File Header

            ```markdown
            ## Vibe Rails Rules
            - Require test coverage minimum 80% (STOP)
            ```

            ## Vibe Control Rules
            - Log all file changes (WARN)
            """);

        await _service.AddRulesAsync(filePath, CancellationToken.None, "Package file changes");

        var rules = await _service.GetRulesWithEnforcementAsync(filePath, CancellationToken.None);
        Assert.Equal(2, rules.Count);
        Assert.Contains(rules, rule => rule.RuleText == "Package file changes");

        var content = await File.ReadAllTextAsync(filePath, TestContext.Current.CancellationToken);
        Assert.Contains("- Require test coverage minimum 80% (STOP)", content);
    }

    [Fact]
    public async Task AddRuleWithEnforcementAsync_AddsAResolvedPathLock()
    {
        var filePath = await CreateTestAgentFile("""
            # Agent File Header
            ## Vibe Rails Rules
            """);

        await _service.AddRuleWithEnforcementAsync(
            filePath,
            "Directory Lock('generated')",
            Enforcement.STOP,
            CancellationToken.None);

        var rule = Assert.Single(await _service.GetRulesWithEnforcementAsync(filePath, CancellationToken.None));
        Assert.Equal("Directory Lock('generated')", rule.RuleText);
        Assert.Equal(Enforcement.STOP, rule.Enforcement);
    }

    [Fact]
    public async Task AddRuleWithEnforcementAsync_RejectsAPathLockThatEscapesTheAgentDirectory()
    {
        var nestedDirectory = Path.Combine(_testDirectory, "nested");
        Directory.CreateDirectory(nestedDirectory);
        var filePath = Path.Combine(nestedDirectory, "vc.rules.md");
        await File.WriteAllTextAsync(
            filePath,
            "## Vibe Rails Rules\n",
            TestContext.Current.CancellationToken);

        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            _service.AddRuleWithEnforcementAsync(
                filePath,
                "File Lock('../outside.txt')",
                Enforcement.STOP,
                TestContext.Current.CancellationToken));

        Assert.Contains("may not escape", error.Message);
    }

    /// <summary>
    /// The lock path is the only place caller-supplied text reaches vc.rules.md verbatim — every
    /// other rule must match a known name exactly. Left unchecked, a line break in it is written
    /// back as two lines, and the second one opening with '#' closes the rules section: every rule
    /// below stops being enforced by the hook AND stops being listed here, so the tampering shows
    /// up as an empty section rather than an error. Refused at the boundary instead.
    /// </summary>
    [Theory]
    [InlineData("File Lock('x\n## Injected')")]
    [InlineData("File Lock('x\r\n## Injected')")]
    [InlineData("Directory Lock('x\n## Vibe Rails Rules')")]
    public async Task AddRuleWithEnforcementAsync_RejectsALockPathCarryingALineBreak(string ruleText)
    {
        var filePath = await CreateTestAgentFile("""
            # Agent File Header
            ## Vibe Rails Rules
            - Never Commit Secrets (STOP)
            """);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _service.AddRuleWithEnforcementAsync(
                filePath,
                ruleText,
                Enforcement.WARN,
                TestContext.Current.CancellationToken));

        // Nothing was written, so the pre-existing rule is still both present and enforced.
        var rule = Assert.Single(
            await _service.GetRulesWithEnforcementAsync(filePath, TestContext.Current.CancellationToken));
        Assert.Equal("Never Commit Secrets", rule.RuleText);
        Assert.Equal(Enforcement.STOP, rule.Enforcement);
        Assert.DoesNotContain(
            "## Injected",
            await File.ReadAllTextAsync(filePath, TestContext.Current.CancellationToken));
    }

    // ===========================================
    // DeleteRulesAsync Tests
    // ===========================================

    [Fact]
    public async Task DeleteRulesAsync_RemovesMatchingRule()
    {
        var filePath = await CreateTestAgentFile(@"# Header
## Vibe Rails Rules
- Log all file changes
- Require test coverage minimum 80%
## Files
");

        await _service.DeleteRulesAsync(filePath, CancellationToken.None, "Log all file changes");

        var rules = await _service.GetRulesAsync(filePath, CancellationToken.None);
        Assert.Single(rules);
        Assert.Equal("Require test coverage minimum 80%", rules[0]);
    }

    [Fact]
    public async Task DeleteRulesAsync_RemovesMultipleRules()
    {
        var filePath = await CreateTestAgentFile(@"# Header
## Vibe Rails Rules
- Log all file changes
- Require test coverage minimum 80%
- Cyclomatic complexity < 20
## Files
");

        await _service.DeleteRulesAsync(filePath, CancellationToken.None, "Log all file changes", "Cyclomatic complexity < 20");

        var rules = await _service.GetRulesAsync(filePath, CancellationToken.None);
        Assert.Single(rules);
        Assert.Equal("Require test coverage minimum 80%", rules[0]);
    }

    [Fact]
    public async Task DeleteRulesAsync_IsCaseInsensitive()
    {
        var filePath = await CreateTestAgentFile(@"# Header
## Vibe Rails Rules
- Log all file changes
## Files
");

        await _service.DeleteRulesAsync(filePath, CancellationToken.None, "LOG ALL FILE CHANGES");

        var rules = await _service.GetRulesAsync(filePath, CancellationToken.None);
        Assert.Empty(rules);
    }

    [Fact]
    public async Task DeleteRulesAsync_DoesNothing_WhenRuleNotFound()
    {
        var filePath = await CreateTestAgentFile(@"# Header
## Vibe Rails Rules
- Log all file changes
## Files
");

        await _service.DeleteRulesAsync(filePath, CancellationToken.None, "Non-existent rule");

        var rules = await _service.GetRulesAsync(filePath, CancellationToken.None);
        Assert.Single(rules);
        Assert.Equal("Log all file changes", rules[0]);
    }

    [Fact]
    public async Task DeleteRulesAsync_PreservesOtherSections()
    {
        var filePath = await CreateTestAgentFile(@"# Agent File Header

## Vibe Rails Rules
- Log all file changes

## Files
- important-file.cs
- another-file.cs

## Prompts
- Custom prompt here
");

        await _service.DeleteRulesAsync(filePath, CancellationToken.None, "Log all file changes");

        var content = await File.ReadAllTextAsync(filePath, TestContext.Current.CancellationToken);
        Assert.Contains("# Agent File Header", content);
        Assert.Contains("## Vibe Rails Rules", content);
        Assert.Contains("## Files", content);
        Assert.Contains("important-file.cs", content);
        Assert.Contains("another-file.cs", content);
        Assert.Contains("## Prompts", content);
        Assert.Contains("Custom prompt here", content);
    }

    [Fact]
    public async Task DeleteRulesAsync_PreservesFileStructure_WhenDeletingAllRules()
    {
        var filePath = await CreateTestAgentFile(@"# Agent File Header

## Vibe Rails Rules
- Log all file changes
- Require test coverage minimum 80%

## Files
- important-file.cs

## Prompts
- Custom prompt
");

        await _service.DeleteRulesAsync(filePath, CancellationToken.None, "Log all file changes", "Require test coverage minimum 80%");

        var content = await File.ReadAllTextAsync(filePath, TestContext.Current.CancellationToken);
        Assert.Contains("# Agent File Header", content);
        Assert.Contains("## Vibe Rails Rules", content);
        Assert.Contains("## Files", content);
        Assert.Contains("important-file.cs", content);
        Assert.Contains("## Prompts", content);
        Assert.Contains("Custom prompt", content);

        var rules = await _service.GetRulesAsync(filePath, CancellationToken.None);
        Assert.Empty(rules);
    }

    [Fact]
    public async Task DeleteRulesAsync_IgnoresFencedRuleExamplesBeforeTheLiveSection()
    {
        var filePath = await CreateTestAgentFile("""
            # Agent File Header

            ```markdown
            ## Vibe Rails Rules
            - Log all file changes (STOP)
            ```

            ## Vibe Rails Rules
            - Log all file changes (WARN)
            """);

        await _service.DeleteRulesAsync(filePath, CancellationToken.None, "Log all file changes");

        Assert.Empty(await _service.GetRulesAsync(filePath, CancellationToken.None));
        var content = await File.ReadAllTextAsync(filePath, TestContext.Current.CancellationToken);
        Assert.Contains("- Log all file changes (STOP)", content);
    }

    [Fact]
    public async Task UpdateRuleEnforcementAsync_IgnoresFencedRuleExamplesBeforeTheLiveSection()
    {
        var filePath = await CreateTestAgentFile("""
            # Agent File Header

            ```markdown
            ## Vibe Rails Rules
            - Log all file changes (STOP)
            ```

            ## Vibe Rails Rules
              - [WARN] Log all file changes
            """);

        await _service.UpdateRuleEnforcementAsync(
            filePath,
            "Log all file changes",
            Enforcement.COMMIT,
            CancellationToken.None);

        var rule = Assert.Single(await _service.GetRulesWithEnforcementAsync(filePath, CancellationToken.None));
        Assert.Equal(Enforcement.COMMIT, rule.Enforcement);

        var content = await File.ReadAllTextAsync(filePath, TestContext.Current.CancellationToken);
        Assert.Contains("- Log all file changes (STOP)", content);
        Assert.Contains("  - Log all file changes (COMMIT)", content);
    }

    // ===========================================
    // Integration Tests - Combined Operations
    // ===========================================

    [Fact]
    public async Task RoundTrip_AddThenDelete_PreservesFileIntegrity()
    {
        var originalContent = @"# Agent File Header

## Vibe Rails Rules

## Files
- existing-file.cs

## Prompts
- Existing prompt
";
        var filePath = await CreateTestAgentFile(originalContent);

        // Add rules
        await _service.AddRulesAsync(filePath, CancellationToken.None, "Log all file changes", "Require test coverage minimum 80%");

        // Verify added
        var rulesAfterAdd = await _service.GetRulesAsync(filePath, CancellationToken.None);
        Assert.Equal(2, rulesAfterAdd.Count);

        // Delete one rule
        await _service.DeleteRulesAsync(filePath, CancellationToken.None, "Log all file changes");

        // Verify state
        var rulesAfterDelete = await _service.GetRulesAsync(filePath, CancellationToken.None);
        Assert.Single(rulesAfterDelete);
        Assert.Equal("Require test coverage minimum 80%", rulesAfterDelete[0]);

        // Verify other sections are intact
        var content = await File.ReadAllTextAsync(filePath, TestContext.Current.CancellationToken);
        Assert.Contains("## Files", content);
        Assert.Contains("existing-file.cs", content);
        Assert.Contains("## Prompts", content);
        Assert.Contains("Existing prompt", content);
    }

    // ===========================================
    // Mock Classes
    // ===========================================

    private class MockRulesService : IRulesService
    {
        private readonly List<string> _rules = new()
        {
            "Log all file changes",
            "Log file changes > 5 lines",
            "Log file changes > 10 lines",
            "Cyclomatic complexity < 20",
            "Cyclomatic complexity < 35",
            "Cyclomatic complexity < 60",
            "Cyclomatic complexity disabled",
            "Require test coverage minimum 50%",
            "Require test coverage minimum 70%",
            "Require test coverage minimum 80%",
            "Require test coverage minimum 100%",
            "Skip test coverage",
            "Package file changes"
        };

        public List<string> AllowedRules() => _rules;

        public List<RuleInfo> AllowedRulesWithDescriptions() =>
            _rules.Select(r => new RuleInfo(r, "Test description")).ToList();

        public string ToDisplayString(Rule value) => RuleParser.ToDisplayString(value);

        public string GetDescription(Rule value) => "Test description";

        public bool TryParse(string value, out Rule rule)
        {
            if (PathLockRule.TryParse(value, out var pathLock))
            {
                rule = pathLock.Kind == PathLockKind.File ? Rule.FileLock : Rule.DirectoryLock;
                return true;
            }

            rule = default;
            var trimmed = value?.Trim();
            if (string.IsNullOrEmpty(trimmed))
                return false;

            // Mirrors the real RuleParser: exact match on the allowed-rule text. Matching a
            // template that merely contains the input let any prefix parse as that rule.
            return _rules.Any(r => r.Equals(trimmed, StringComparison.OrdinalIgnoreCase));
        }
    }

    private class MockGitService : IGitService
    {
        private readonly string _rootPath;

        public MockGitService(string rootPath)
        {
            _rootPath = rootPath;
        }

        public Task<string> GetRootPathAsync(CancellationToken cancellationToken) => Task.FromResult(_rootPath);

        public Task<List<string>> GetChangedFileAsync(CancellationToken cancellationToken) => Task.FromResult(new List<string>());

        public Task<List<string>> GetStagedFilesAsync(CancellationToken cancellationToken) => Task.FromResult(new List<string>());

        public Task StageFileAsync(string path, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<bool> IsStagingSafeAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<string?> GetCurrentCommitHashAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);

        public Task<string?> GetCurrentBranchAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);

        public Task<List<FileChangeInfo>> GetFileChangesSinceAsync(string commitHash, CancellationToken cancellationToken = default) => Task.FromResult(new List<FileChangeInfo>());

        public Task<string?> GetRemoteUrlAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    }
}
