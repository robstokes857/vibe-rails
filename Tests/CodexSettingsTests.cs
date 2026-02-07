using VibeRails.DTOs;
using VibeRails.Interfaces;
using VibeRails.Services;
using VibeRails.Services.LlmClis;
using Xunit;

namespace Tests;

public class CodexSettingsTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly CodexLlmCliEnvironment _service;
    private readonly MockFileService _mockFileService;
    private readonly MockDbService _mockDbService;

    public CodexSettingsTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"CodexSettingsTests_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDirectory);

        // Set up environment variable for test
        Environment.SetEnvironmentVariable("VIBE_CONTROL_ENVPATH", _testDirectory);

        _mockFileService = new MockFileService();
        _mockDbService = new MockDbService();

        _service = new CodexLlmCliEnvironment(_mockDbService, _mockFileService);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("VIBE_CONTROL_ENVPATH", null);
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
    }

    // ===========================================
    // GetSettings Tests
    // ===========================================

    [Fact]
    public async Task GetSettings_ReturnsDefaults_WhenFileNotExists()
    {
        _mockFileService.SetFileExists(false);

        var settings = await _service.GetSettings("test-env", CancellationToken.None);

        Assert.Equal("", settings.Model);
        Assert.Equal("read-only", settings.Sandbox);
        Assert.Equal("untrusted", settings.Approval);
        Assert.False(settings.FullAuto);
        Assert.False(settings.Search);
    }

    [Fact]
    public async Task GetSettings_ReadsAllValues_FromValidToml()
    {
        var toml = @"
model = ""o3""
sandbox = ""workspace-write""
approval = ""on-request""
full_auto = true
search = true
";

        _mockFileService.SetFileExists(true);
        _mockFileService.SetFileContent(toml);

        var settings = await _service.GetSettings("test-env", CancellationToken.None);

        Assert.Equal("o3", settings.Model);
        Assert.Equal("workspace-write", settings.Sandbox);
        Assert.Equal("on-request", settings.Approval);
        Assert.True(settings.FullAuto);
        Assert.True(settings.Search);
    }

    [Fact]
    public async Task GetSettings_ReturnsDefaults_ForMissingFields()
    {
        var toml = @"
model = ""gpt-5-codex""
";

        _mockFileService.SetFileExists(true);
        _mockFileService.SetFileContent(toml);

        var settings = await _service.GetSettings("test-env", CancellationToken.None);

        Assert.Equal("gpt-5-codex", settings.Model);
        Assert.Equal("read-only", settings.Sandbox); // Default
        Assert.Equal("untrusted", settings.Approval); // Default
        Assert.False(settings.FullAuto); // Default
        Assert.False(settings.Search); // Default
    }

    [Fact]
    public async Task GetSettings_HandlesQuotedAndUnquotedValues()
    {
        var toml = @"
model = o3
sandbox = ""workspace-write""
approval = 'on-failure'
";

        _mockFileService.SetFileExists(true);
        _mockFileService.SetFileContent(toml);

        var settings = await _service.GetSettings("test-env", CancellationToken.None);

        Assert.Equal("o3", settings.Model);
        Assert.Equal("workspace-write", settings.Sandbox);
        Assert.Equal("on-failure", settings.Approval);
    }

    [Fact]
    public async Task GetSettings_HandlesBooleanValues()
    {
        var toml = @"
full_auto = true
search = false
";

        _mockFileService.SetFileExists(true);
        _mockFileService.SetFileContent(toml);

        var settings = await _service.GetSettings("test-env", CancellationToken.None);

        Assert.True(settings.FullAuto);
        Assert.False(settings.Search);
    }

    // ===========================================
    // SaveSettings Tests
    // ===========================================

    [Fact]
    public async Task SaveSettings_WritesAllValues_ToToml()
    {
        _mockFileService.SetFileExists(false);

        var settings = new CodexSettingsDto
        {
            Model = "o3",
            Sandbox = "workspace-write",
            Approval = "on-request",
            FullAuto = true,
            Search = true
        };

        await _service.SaveSettings("test-env", settings, CancellationToken.None);

        var writtenContent = _mockFileService.GetWrittenContent();
        Assert.Contains("model = \"o3\"", writtenContent);
        Assert.Contains("sandbox = \"workspace-write\"", writtenContent);
        Assert.Contains("approval = \"on-request\"", writtenContent);
        Assert.Contains("full_auto = true", writtenContent);
        Assert.Contains("search = true", writtenContent);
    }

    [Fact]
    public async Task SaveSettings_PreservesExistingContent()
    {
        var existingToml = @"# Codex CLI Configuration
# Custom comment

model = ""old-model""
custom_field = ""preserved""
";

        _mockFileService.SetFileExists(true);
        _mockFileService.SetFileContent(existingToml);

        var settings = new CodexSettingsDto
        {
            Model = "new-model",
            Sandbox = "read-only"
        };

        await _service.SaveSettings("test-env", settings, CancellationToken.None);

        var writtenContent = _mockFileService.GetWrittenContent();
        Assert.Contains("model = \"new-model\"", writtenContent); // Updated
        Assert.Contains("custom_field = \"preserved\"", writtenContent); // Preserved
        Assert.Contains("# Custom comment", writtenContent); // Comments preserved
    }

    [Fact]
    public async Task SaveSettings_UpdatesExistingValues()
    {
        var existingToml = @"
model = ""old-model""
sandbox = ""read-only""
full_auto = false
";

        _mockFileService.SetFileExists(true);
        _mockFileService.SetFileContent(existingToml);

        var settings = new CodexSettingsDto
        {
            Model = "new-model",
            Sandbox = "danger-full-access",
            FullAuto = true,
            Search = false
        };

        await _service.SaveSettings("test-env", settings, CancellationToken.None);

        var writtenContent = _mockFileService.GetWrittenContent();
        Assert.Contains("model = \"new-model\"", writtenContent);
        Assert.Contains("sandbox = \"danger-full-access\"", writtenContent);
        Assert.Contains("full_auto = true", writtenContent);
    }

    [Fact]
    public async Task SaveSettings_RemovesEmptyModel()
    {
        var existingToml = @"
model = ""old-model""
sandbox = ""read-only""
";

        _mockFileService.SetFileExists(true);
        _mockFileService.SetFileContent(existingToml);

        var settings = new CodexSettingsDto
        {
            Model = "", // Empty - should be removed
            Sandbox = "workspace-write"
        };

        await _service.SaveSettings("test-env", settings, CancellationToken.None);

        var writtenContent = _mockFileService.GetWrittenContent();
        Assert.DoesNotContain("model =", writtenContent);
        Assert.Contains("sandbox = \"workspace-write\"", writtenContent);
    }

    [Fact]
    public async Task SaveSettings_AddsNewFieldsAtEnd()
    {
        var existingToml = @"# Header comment
model = ""o3""
";

        _mockFileService.SetFileExists(true);
        _mockFileService.SetFileContent(existingToml);

        var settings = new CodexSettingsDto
        {
            Model = "o3",
            Sandbox = "workspace-write",
            Search = true
        };

        await _service.SaveSettings("test-env", settings, CancellationToken.None);

        var writtenContent = _mockFileService.GetWrittenContent();
        Assert.Contains("sandbox = \"workspace-write\"", writtenContent);
        Assert.Contains("search = true", writtenContent);
    }

    // ===========================================
    // Mock Classes
    // ===========================================

    private class MockFileService : IFileService
    {
        private bool _fileExists = false;
        private string _fileContent = "";
        private string _writtenContent = "";

        public void SetFileExists(bool exists) => _fileExists = exists;
        public void SetFileContent(string content) => _fileContent = content;
        public string GetWrittenContent() => _writtenContent;

        public bool FileExists(string path) => _fileExists;

        public Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken)
            => Task.FromResult(_fileContent);

        public Task WriteAllTextAsync(string path, string content, FileMode mode, FileShare share, CancellationToken cancellationToken)
        {
            _writtenContent = content;
            return Task.CompletedTask;
        }

        // Unused interface methods - minimal implementations
        public (bool inGet, string projectRoot) TryGetProjectRootPath() => (false, "");
        public void InitGlobalSave() { }
        public void InitLocal(string rootPath) { }
        public Task AppendAllTextAsync(string path, string content, CancellationToken cancellationToken) => Task.CompletedTask;
        public string Combine(params string[] paths) => Path.Combine(paths);
        public void CopyFile(string sourceFileName, string destFileName, bool overwrite) { }
        public void CreateDirectory(string path) { }
        public void DeleteDirectory(string path, bool recursive) { }
        public void DeleteFile(string path) { }
        public bool DirectoryExists(string path) => true;
        public IEnumerable<string> EnumerateDirectories(string path, string searchPattern, EnumerationOptions options) => Array.Empty<string>();
        public IEnumerable<string> EnumerateFiles(string path, string searchPattern, EnumerationOptions options) => Array.Empty<string>();
        public string GetCurrentDirectory() => "";
        public string GetDirectoryName(string? path) => Path.GetDirectoryName(path) ?? "";
        public string GetFileName(string? path) => Path.GetFileName(path) ?? "";
        public string GetFileNameWithoutExtension(string? path) => Path.GetFileNameWithoutExtension(path) ?? "";
        public string GetGlobalSavePath() => "";
        public string GetTempPath() => Path.GetTempPath();
        public string GetUserProfilePath() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    private class MockDbService : IDbService
    {
        public void InitializeDatabase() { }

        public Task CreateSessionAsync(string sessionId, string cli, string? envName, string workDir)
            => Task.CompletedTask;

        public Task LogSessionOutputAsync(string sessionId, string content, bool isError = false)
            => Task.CompletedTask;

        public Task CompleteSessionAsync(string sessionId, int exitCode)
            => Task.CompletedTask;

        public Task<SessionWithLogsResponse?> GetSessionWithLogsAsync(string sessionId, CancellationToken cancellationToken)
            => Task.FromResult<SessionWithLogsResponse?>(null);

        public Task<List<SessionResponse>> GetRecentSessionsAsync(int limit, CancellationToken cancellationToken)
            => Task.FromResult(new List<SessionResponse>());

        public Task<UserInputRecord?> GetLastUserInputAsync(string sessionId)
            => Task.FromResult<UserInputRecord?>(null);

        public Task<long> InsertUserInputAsync(string sessionId, int sequence, string inputText, string? gitCommitHash)
            => Task.FromResult(0L);

        public Task InsertFileChangesAsync(long userInputId, long? previousInputId, List<FileChangeInfo> changes)
            => Task.CompletedTask;

        public Task RecordUserInputAsync(string sessionId, string inputText, IGitService gitService, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<long> CreateClaudePlanAsync(string sessionId, long? userInputId, string? planFilePath, string planContent, string? summary)
            => Task.FromResult(0L);

        public Task<ClaudePlanRecord?> GetClaudePlanAsync(long planId, CancellationToken cancellationToken)
            => Task.FromResult<ClaudePlanRecord?>(null);

        public Task<List<ClaudePlanRecord>> GetClaudePlansForSessionAsync(string sessionId, CancellationToken cancellationToken)
            => Task.FromResult(new List<ClaudePlanRecord>());

        public Task<List<ClaudePlanRecord>> GetRecentClaudePlansAsync(int limit, CancellationToken cancellationToken)
            => Task.FromResult(new List<ClaudePlanRecord>());

        public Task UpdateClaudePlanStatusAsync(long planId, string status)
            => Task.CompletedTask;

        public Task CompleteClaudePlanAsync(long planId)
            => Task.CompletedTask;
    }
}
