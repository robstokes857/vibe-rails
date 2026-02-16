using VibeRails.DTOs;
using VibeRails.Services;
using Xunit;

namespace Tests;

public class IntegrationAgentFileTests 
{
    private readonly string _testDirectory;
    private readonly string _templatePath;
    private readonly string _agentFilePath;
    private readonly AgentFileService _service;

    public IntegrationAgentFileTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"IntegrationAgentFileTests_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDirectory);

        // Path to the template file copied to output directory
        _templatePath = Path.Combine(AppContext.BaseDirectory, "test_agent.txt");
        _agentFilePath = Path.Combine(_testDirectory, "AGENTS.md");

        var rulesService = new RulesService();
        var gitService = new MockGitService(_testDirectory);
        _service = new AgentFileService(gitService, rulesService);
    }

  

    private async Task SetupAgentFile()
    {
        File.Copy(_templatePath, _agentFilePath, overwrite: true);
    }

    [Fact]
    public async Task GetRulesAsync_ReturnsRulesFromTemplateFile()
    {
        await SetupAgentFile();

        var rules = await _service.GetRulesAsync(_agentFilePath, CancellationToken.None);

        Assert.Equal(3, rules.Count);
        Assert.Contains("Log all file changes", rules);
        Assert.Contains("Cyclomatic complexity < 35", rules);
        Assert.Contains("Require test coverage minimum 70%", rules);
    }

    [Fact]
    public async Task AddRulesAsync_AddsRuleToTemplateFile()
    {
        await SetupAgentFile();

        await _service.AddRulesAsync(_agentFilePath, CancellationToken.None, "Cyclomatic complexity < 20");

        var rules = await _service.GetRulesAsync(_agentFilePath, CancellationToken.None);
        Assert.Equal(4, rules.Count);
        Assert.Contains("Cyclomatic complexity < 20", rules);

        // Verify file structure is preserved
        var content = await File.ReadAllTextAsync(_agentFilePath);
        Assert.Contains("## Files", content);
        Assert.Contains("important-file.cs", content);
        Assert.Contains("## Prompts", content);
    }

    [Fact]
    public async Task DeleteRulesAsync_RemovesRuleFromTemplateFile()
    {
        await SetupAgentFile();

        await _service.DeleteRulesAsync(_agentFilePath, CancellationToken.None, "Log all file changes");

        var rules = await _service.GetRulesAsync(_agentFilePath, CancellationToken.None);
        Assert.Equal(2, rules.Count);
        Assert.DoesNotContain("Log all file changes", rules);
        Assert.Contains("Cyclomatic complexity < 35", rules);
        Assert.Contains("Require test coverage minimum 70%", rules);
    }

    [Fact]
    public async Task AddAndDeleteRules_WorksCorrectly()
    {
        await SetupAgentFile();

        // Add a rule
        await _service.AddRulesAsync(_agentFilePath, CancellationToken.None, "Log file changes > 5 lines");

        var rulesAfterAdd = await _service.GetRulesAsync(_agentFilePath, CancellationToken.None);
        Assert.Equal(4, rulesAfterAdd.Count);

        // Delete original rules
        await _service.DeleteRulesAsync(_agentFilePath, CancellationToken.None,
            "Log all file changes",
            "Cyclomatic complexity < 35");

        var rulesAfterDelete = await _service.GetRulesAsync(_agentFilePath, CancellationToken.None);
        Assert.Equal(2, rulesAfterDelete.Count);
        Assert.Contains("Log file changes > 5 lines", rulesAfterDelete);
        Assert.Contains("Require test coverage minimum 70%", rulesAfterDelete);

        // Verify file structure
        var content = await File.ReadAllTextAsync(_agentFilePath);
        Assert.Contains("# Repository Guidelines", content);
        Assert.Contains("## Development Guidelines", content);
        Assert.Contains("## Vibe Rails Rules", content);
        Assert.Contains("## Files", content);
        Assert.Contains("## Prompts", content);
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

        public Task<string?> GetCurrentBranchAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);

        public Task<List<FileChangeInfo>> GetFileChangesSinceAsync(string commitHash, CancellationToken cancellationToken = default) => Task.FromResult(new List<FileChangeInfo>());
    }
}
