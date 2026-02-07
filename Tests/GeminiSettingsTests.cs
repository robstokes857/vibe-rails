using VibeRails.DTOs;
using VibeRails.Interfaces;
using VibeRails.Services;
using VibeRails.Services.LlmClis;
using Xunit;

namespace Tests;

public class GeminiSettingsTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly GeminiLlmCliEnvironment _service;
    private readonly MockFileService _mockFileService;
    private readonly MockDbService _mockDbService;

    public GeminiSettingsTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"GeminiSettingsTests_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDirectory);

        // Set up environment variable for test
        Environment.SetEnvironmentVariable("VIBE_CONTROL_ENVPATH", _testDirectory);

        _mockFileService = new MockFileService();
        _mockDbService = new MockDbService();
        var mcpSettings = new McpSettings("");

        _service = new GeminiLlmCliEnvironment(_mockDbService, _mockFileService, mcpSettings);
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

        Assert.Equal("Default", settings.Theme);
        Assert.True(settings.SandboxEnabled);
        Assert.False(settings.AutoApproveTools);
        Assert.False(settings.VimMode);
        Assert.True(settings.CheckForUpdates);
        Assert.False(settings.YoloMode);
    }

    [Fact]
    public async Task GetSettings_ReadsAllValues_FromValidJson()
    {
        var json = @"{
            ""theme"": ""Dark"",
            ""checkForUpdates"": false,
            ""general"": {
                ""vimMode"": true
            },
            ""sandbox"": {
                ""enabled"": false
            },
            ""tools"": {
                ""autoAccept"": true
            },
            ""security"": {
                ""disableYoloMode"": false
            }
        }";

        _mockFileService.SetFileExists(true);
        _mockFileService.SetFileContent(json);

        var settings = await _service.GetSettings("test-env", CancellationToken.None);

        Assert.Equal("Dark", settings.Theme);
        Assert.False(settings.SandboxEnabled);
        Assert.True(settings.AutoApproveTools);
        Assert.True(settings.VimMode);
        Assert.False(settings.CheckForUpdates);
        Assert.True(settings.YoloMode); // Inverted from disableYoloMode=false
    }

    [Fact]
    public async Task GetSettings_ReturnsDefaults_ForMissingFields()
    {
        var json = @"{
            ""theme"": ""Light"",
            ""someOtherField"": ""value""
        }";

        _mockFileService.SetFileExists(true);
        _mockFileService.SetFileContent(json);

        var settings = await _service.GetSettings("test-env", CancellationToken.None);

        Assert.Equal("Light", settings.Theme);
        Assert.True(settings.SandboxEnabled); // Default
        Assert.False(settings.AutoApproveTools); // Default
        Assert.False(settings.VimMode); // Default
        Assert.True(settings.CheckForUpdates); // Default
        Assert.False(settings.YoloMode); // Default (disableYoloMode defaults to true)
    }

    [Fact]
    public async Task GetSettings_HandlesPartialNestedObjects()
    {
        var json = @"{
            ""general"": {},
            ""sandbox"": {
                ""enabled"": false
            }
        }";

        _mockFileService.SetFileExists(true);
        _mockFileService.SetFileContent(json);

        var settings = await _service.GetSettings("test-env", CancellationToken.None);

        Assert.Equal("Default", settings.Theme);
        Assert.False(settings.SandboxEnabled);
        Assert.False(settings.VimMode); // general.vimMode not present
    }

    [Fact]
    public async Task GetSettings_YoloModeIsInverted_FromDisableYoloMode()
    {
        // When disableYoloMode is true, YoloMode should be false
        var jsonDisabled = @"{ ""security"": { ""disableYoloMode"": true } }";
        _mockFileService.SetFileExists(true);
        _mockFileService.SetFileContent(jsonDisabled);

        var settings1 = await _service.GetSettings("test-env", CancellationToken.None);
        Assert.False(settings1.YoloMode);

        // When disableYoloMode is false, YoloMode should be true
        var jsonEnabled = @"{ ""security"": { ""disableYoloMode"": false } }";
        _mockFileService.SetFileContent(jsonEnabled);

        var settings2 = await _service.GetSettings("test-env", CancellationToken.None);
        Assert.True(settings2.YoloMode);
    }

    // ===========================================
    // SaveSettings Tests
    // ===========================================

    [Fact]
    public async Task SaveSettings_WritesAllValues_ToJson()
    {
        _mockFileService.SetFileExists(false);

        var settings = new GeminiSettingsDto
        {
            Theme = "Dark",
            SandboxEnabled = false,
            AutoApproveTools = true,
            VimMode = true,
            CheckForUpdates = false,
            YoloMode = true
        };

        await _service.SaveSettings("test-env", settings, CancellationToken.None);

        var writtenJson = _mockFileService.GetWrittenContent();
        Assert.Contains("\"theme\": \"Dark\"", writtenJson);
        Assert.Contains("\"checkForUpdates\": false", writtenJson);
        Assert.Contains("\"vimMode\": true", writtenJson);
        Assert.Contains("\"enabled\": false", writtenJson); // sandbox.enabled
        Assert.Contains("\"autoAccept\": true", writtenJson); // tools.autoAccept
        Assert.Contains("\"disableYoloMode\": false", writtenJson); // Inverted from YoloMode=true
    }

    [Fact]
    public async Task SaveSettings_PreservesExistingFields()
    {
        var existingJson = @"{
            ""theme"": ""Dark"",
            ""selectedAuthType"": ""oauth-personal"",
            ""customField"": ""preserved""
        }";

        _mockFileService.SetFileExists(true);
        _mockFileService.SetFileContent(existingJson);

        var settings = new GeminiSettingsDto
        {
            Theme = "Light",
            SandboxEnabled = true
        };

        await _service.SaveSettings("test-env", settings, CancellationToken.None);

        var writtenJson = _mockFileService.GetWrittenContent();
        Assert.Contains("\"theme\": \"Light\"", writtenJson); // Updated
        Assert.Contains("\"selectedAuthType\": \"oauth-personal\"", writtenJson); // Preserved
        Assert.Contains("\"customField\": \"preserved\"", writtenJson); // Preserved
    }

    [Fact]
    public async Task SaveSettings_CreatesNestedObjects_WhenMissing()
    {
        _mockFileService.SetFileExists(false);

        var settings = new GeminiSettingsDto
        {
            VimMode = true,
            SandboxEnabled = false
        };

        await _service.SaveSettings("test-env", settings, CancellationToken.None);

        var writtenJson = _mockFileService.GetWrittenContent();
        Assert.Contains("\"general\":", writtenJson);
        Assert.Contains("\"sandbox\":", writtenJson);
        Assert.Contains("\"tools\":", writtenJson);
        Assert.Contains("\"security\":", writtenJson);
    }

    [Fact]
    public async Task SaveSettings_YoloModeInverts_ToDisableYoloMode()
    {
        _mockFileService.SetFileExists(false);

        // YoloMode=true should write disableYoloMode=false
        var settings1 = new GeminiSettingsDto { YoloMode = true };
        await _service.SaveSettings("test-env", settings1, CancellationToken.None);
        Assert.Contains("\"disableYoloMode\": false", _mockFileService.GetWrittenContent());

        // YoloMode=false should write disableYoloMode=true
        var settings2 = new GeminiSettingsDto { YoloMode = false };
        await _service.SaveSettings("test-env", settings2, CancellationToken.None);
        Assert.Contains("\"disableYoloMode\": true", _mockFileService.GetWrittenContent());
    }

    [Fact]
    public async Task SaveSettings_MergesIntoExistingNestedObjects()
    {
        var existingJson = @"{
            ""general"": {
                ""someOtherSetting"": true
            }
        }";

        _mockFileService.SetFileExists(true);
        _mockFileService.SetFileContent(existingJson);

        var settings = new GeminiSettingsDto { VimMode = true };

        await _service.SaveSettings("test-env", settings, CancellationToken.None);

        var writtenJson = _mockFileService.GetWrittenContent();
        Assert.Contains("\"vimMode\": true", writtenJson);
        Assert.Contains("\"someOtherSetting\": true", writtenJson); // Preserved
    }

    // ===========================================
    // Mock Classes
    // ===========================================

    private class MockFileService : IFileService
    {
        private bool _fileExists = false;
        private string _fileContent = "{}";
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
