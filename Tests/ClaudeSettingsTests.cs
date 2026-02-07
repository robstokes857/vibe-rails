using VibeRails.DTOs;
using VibeRails.Interfaces;
using VibeRails.Services;
using VibeRails.Services.LlmClis;
using Xunit;

namespace Tests;

public class ClaudeSettingsTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly ClaudeLlmCliEnvironment _service;
    private readonly MockFileService _mockFileService;
    private readonly MockDbService _mockDbService;

    public ClaudeSettingsTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"ClaudeSettingsTests_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDirectory);

        // Set up environment variable for test
        Environment.SetEnvironmentVariable("VIBE_CONTROL_ENVPATH", _testDirectory);

        _mockFileService = new MockFileService();
        _mockDbService = new MockDbService();

        _service = new ClaudeLlmCliEnvironment(_mockDbService, _mockFileService);
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
        Assert.Equal("default", settings.PermissionMode);
        Assert.Equal("", settings.AllowedTools);
        Assert.Equal("", settings.DisallowedTools);
        Assert.False(settings.SkipPermissions);
        Assert.False(settings.Verbose);
    }

    [Fact]
    public async Task GetSettings_ReadsAllValues_FromValidJson()
    {
        var json = @"{
    ""model"": ""opus"",
    ""permissionMode"": ""plan"",
    ""allowedTools"": ""Read,Glob,Grep"",
    ""disallowedTools"": ""Bash"",
    ""skipPermissions"": true,
    ""verbose"": true
}";

        _mockFileService.SetFileExists(true);
        _mockFileService.SetFileContent(json);

        var settings = await _service.GetSettings("test-env", CancellationToken.None);

        Assert.Equal("opus", settings.Model);
        Assert.Equal("plan", settings.PermissionMode);
        Assert.Equal("Read,Glob,Grep", settings.AllowedTools);
        Assert.Equal("Bash", settings.DisallowedTools);
        Assert.True(settings.SkipPermissions);
        Assert.True(settings.Verbose);
    }

    [Fact]
    public async Task GetSettings_ReturnsDefaults_ForMissingFields()
    {
        var json = @"{
    ""model"": ""sonnet""
}";

        _mockFileService.SetFileExists(true);
        _mockFileService.SetFileContent(json);

        var settings = await _service.GetSettings("test-env", CancellationToken.None);

        Assert.Equal("sonnet", settings.Model);
        Assert.Equal("default", settings.PermissionMode); // Default
        Assert.Equal("", settings.AllowedTools); // Default
        Assert.Equal("", settings.DisallowedTools); // Default
        Assert.False(settings.SkipPermissions); // Default
        Assert.False(settings.Verbose); // Default
    }

    [Fact]
    public async Task GetSettings_HandlesEmptyJson()
    {
        var json = "{}";

        _mockFileService.SetFileExists(true);
        _mockFileService.SetFileContent(json);

        var settings = await _service.GetSettings("test-env", CancellationToken.None);

        Assert.Equal("", settings.Model);
        Assert.Equal("default", settings.PermissionMode);
        Assert.False(settings.SkipPermissions);
        Assert.False(settings.Verbose);
    }

    [Fact]
    public async Task GetSettings_HandlesBooleanValues()
    {
        var json = @"{
    ""skipPermissions"": true,
    ""verbose"": false
}";

        _mockFileService.SetFileExists(true);
        _mockFileService.SetFileContent(json);

        var settings = await _service.GetSettings("test-env", CancellationToken.None);

        Assert.True(settings.SkipPermissions);
        Assert.False(settings.Verbose);
    }

    // ===========================================
    // SaveSettings Tests
    // ===========================================

    [Fact]
    public async Task SaveSettings_WritesAllValues_ToJson()
    {
        _mockFileService.SetFileExists(false);

        var settings = new ClaudeSettingsDto
        {
            Model = "opus",
            PermissionMode = "plan",
            AllowedTools = "Read,Glob",
            DisallowedTools = "Bash",
            SkipPermissions = true,
            Verbose = true
        };

        await _service.SaveSettings("test-env", settings, CancellationToken.None);

        var writtenContent = _mockFileService.GetWrittenContent();
        Assert.Contains("\"model\": \"opus\"", writtenContent);
        Assert.Contains("\"permissionMode\": \"plan\"", writtenContent);
        Assert.Contains("\"allowedTools\": \"Read,Glob\"", writtenContent);
        Assert.Contains("\"disallowedTools\": \"Bash\"", writtenContent);
        Assert.Contains("\"skipPermissions\": true", writtenContent);
        Assert.Contains("\"verbose\": true", writtenContent);
    }

    [Fact]
    public async Task SaveSettings_PreservesExistingContent()
    {
        var existingJson = @"{
    ""permissions"": { ""allow"": [""read""] },
    ""model"": ""old-model"",
    ""customField"": ""preserved""
}";

        _mockFileService.SetFileExists(true);
        _mockFileService.SetFileContent(existingJson);

        var settings = new ClaudeSettingsDto
        {
            Model = "new-model",
            PermissionMode = "default"
        };

        await _service.SaveSettings("test-env", settings, CancellationToken.None);

        var writtenContent = _mockFileService.GetWrittenContent();
        Assert.Contains("\"model\": \"new-model\"", writtenContent); // Updated
        Assert.Contains("\"customField\": \"preserved\"", writtenContent); // Preserved
        Assert.Contains("\"permissions\"", writtenContent); // Preserved
    }

    [Fact]
    public async Task SaveSettings_UpdatesExistingValues()
    {
        var existingJson = @"{
    ""model"": ""old-model"",
    ""permissionMode"": ""default"",
    ""skipPermissions"": false
}";

        _mockFileService.SetFileExists(true);
        _mockFileService.SetFileContent(existingJson);

        var settings = new ClaudeSettingsDto
        {
            Model = "new-model",
            PermissionMode = "bypassPermissions",
            SkipPermissions = true,
            Verbose = false
        };

        await _service.SaveSettings("test-env", settings, CancellationToken.None);

        var writtenContent = _mockFileService.GetWrittenContent();
        Assert.Contains("\"model\": \"new-model\"", writtenContent);
        Assert.Contains("\"permissionMode\": \"bypassPermissions\"", writtenContent);
        Assert.Contains("\"skipPermissions\": true", writtenContent);
    }

    [Fact]
    public async Task SaveSettings_RemovesEmptyAndDefaultValues()
    {
        var existingJson = @"{
    ""model"": ""old-model"",
    ""permissionMode"": ""plan""
}";

        _mockFileService.SetFileExists(true);
        _mockFileService.SetFileContent(existingJson);

        var settings = new ClaudeSettingsDto
        {
            Model = "", // Empty - should be removed
            PermissionMode = "default" // Default - should be removed
        };

        await _service.SaveSettings("test-env", settings, CancellationToken.None);

        var writtenContent = _mockFileService.GetWrittenContent();
        Assert.DoesNotContain("\"model\"", writtenContent);
        Assert.DoesNotContain("\"permissionMode\"", writtenContent);
    }

    [Fact]
    public async Task SaveSettings_AddsNewFieldsToJson()
    {
        var existingJson = @"{
    ""model"": ""sonnet""
}";

        _mockFileService.SetFileExists(true);
        _mockFileService.SetFileContent(existingJson);

        var settings = new ClaudeSettingsDto
        {
            Model = "sonnet",
            AllowedTools = "Read,Write",
            Verbose = true
        };

        await _service.SaveSettings("test-env", settings, CancellationToken.None);

        var writtenContent = _mockFileService.GetWrittenContent();
        Assert.Contains("\"allowedTools\": \"Read,Write\"", writtenContent);
        Assert.Contains("\"verbose\": true", writtenContent);
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
