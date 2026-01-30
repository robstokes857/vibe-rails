using VibeRails.DTOs;
using VibeRails.Services;
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
        var filePath = Path.Combine(_testDirectory, "agent.md");
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

        var content = await File.ReadAllTextAsync(filePath);
        Assert.Contains("## Files", content);
        Assert.Contains("important-file.cs", content);
        Assert.Contains("another-file.cs", content);
        Assert.Contains("## Prompts", content);
        Assert.Contains("Custom prompt here", content);
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

        var content = await File.ReadAllTextAsync(filePath);
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

        var content = await File.ReadAllTextAsync(filePath);
        Assert.Contains("# Agent File Header", content);
        Assert.Contains("## Vibe Rails Rules", content);
        Assert.Contains("## Files", content);
        Assert.Contains("important-file.cs", content);
        Assert.Contains("## Prompts", content);
        Assert.Contains("Custom prompt", content);

        var rules = await _service.GetRulesAsync(filePath, CancellationToken.None);
        Assert.Empty(rules);
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
        var content = await File.ReadAllTextAsync(filePath);
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
            "Require test coverage minimum 80% minimum 50%",
            "Require test coverage minimum 80% minimum 70%",
            "Require test coverage minimum 80% minimum 80%",
            "Require test coverage minimum 80% minimum 100%",
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
            rule = default;
            var trimmed = value?.Trim();
            if (string.IsNullOrEmpty(trimmed))
                return false;

            // Match the real RulesService behavior - use Contains for fuzzy matching
            foreach (var r in _rules)
            {
                if (r.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
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

        public Task<string?> GetCurrentCommitHashAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);

        public Task<List<FileChangeInfo>> GetFileChangesSinceAsync(string commitHash, CancellationToken cancellationToken = default) => Task.FromResult(new List<FileChangeInfo>());
    }
}
