using VibeRails.DTOs;
using VibeRails.Interfaces;
using VibeRails.Services.LlmClis;
using Xunit;

namespace Tests;

public class ClaudeSettingsTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly ClaudeLlmCliEnvironment _service;
    private readonly MockFileService _mockFileService;

    public ClaudeSettingsTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"ClaudeSettingsTests_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDirectory);

        Environment.SetEnvironmentVariable("VIBE_CONTROL_ENVPATH", _testDirectory);

        _mockFileService = new MockFileService();
        _service = new ClaudeLlmCliEnvironment(_mockFileService);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("VIBE_CONTROL_ENVPATH", null);
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task GetSettings_ReturnsDefaults_WhenFileNotExists()
    {
        _mockFileService.SetFileExists(false);

        var settings = await _service.GetSettings("test-env", CancellationToken.None);

        Assert.Equal("", settings.Effort);
        Assert.False(settings.NoSessionPersistence);
        Assert.Equal("default", settings.PermissionMode);
        Assert.Equal("", settings.SystemPrompt);
        Assert.False(settings.AllowDangerouslySkipPermissions);
        Assert.Equal("", settings.DangerouslyLoadDevelopmentChannels);
        Assert.False(settings.DangerouslySkipPermissions);
        Assert.Equal("", settings.AllowedTools);
        Assert.Equal("", settings.AppendSystemPrompt);
        Assert.False(settings.Bare);
        Assert.Equal("", settings.Betas);
        Assert.Equal("", settings.Channels);
        Assert.False(settings.Debug);
        Assert.Equal("", settings.DebugFilter);
    }

    [Fact]
    public async Task GetSettings_ReadsAllSupportedValues_FromValidJson()
    {
        var json = @"{
    ""effort"": ""high"",
    ""noSessionPersistence"": true,
    ""permissionMode"": ""plan"",
    ""systemPrompt"": ""You are a Python expert"",
    ""allowDangerouslySkipPermissions"": true,
    ""dangerouslyLoadDevelopmentChannels"": ""server:webhook plugin:test@local"",
    ""dangerouslySkipPermissions"": true,
    ""allowedTools"": ""Bash(git log *)\nRead"",
    ""appendSystemPrompt"": ""Always use TypeScript"",
    ""bare"": true,
    ""betas"": ""interleaved-thinking"",
    ""channels"": ""plugin:my-notifier@my-marketplace"",
    ""debug"": true,
    ""debugFilter"": ""api,mcp""
}";

        _mockFileService.SetFileExists(true);
        _mockFileService.SetFileContent(json);

        var settings = await _service.GetSettings("test-env", CancellationToken.None);

        Assert.Equal("high", settings.Effort);
        Assert.True(settings.NoSessionPersistence);
        Assert.Equal("plan", settings.PermissionMode);
        Assert.Equal("You are a Python expert", settings.SystemPrompt);
        Assert.True(settings.AllowDangerouslySkipPermissions);
        Assert.Equal("server:webhook plugin:test@local", settings.DangerouslyLoadDevelopmentChannels);
        Assert.True(settings.DangerouslySkipPermissions);
        Assert.Equal("Bash(git log *)\nRead", settings.AllowedTools);
        Assert.Equal("Always use TypeScript", settings.AppendSystemPrompt);
        Assert.True(settings.Bare);
        Assert.Equal("interleaved-thinking", settings.Betas);
        Assert.Equal("plugin:my-notifier@my-marketplace", settings.Channels);
        Assert.True(settings.Debug);
        Assert.Equal("api,mcp", settings.DebugFilter);
    }

    [Fact]
    public async Task GetSettings_MapsLegacySkipPermissions_ToDangerouslySkipPermissions()
    {
        var json = @"{
    ""skipPermissions"": true
}";

        _mockFileService.SetFileExists(true);
        _mockFileService.SetFileContent(json);

        var settings = await _service.GetSettings("test-env", CancellationToken.None);

        Assert.True(settings.DangerouslySkipPermissions);
    }

    [Fact]
    public async Task GetSettings_ReturnsDefaults_ForMissingFields()
    {
        var json = @"{
    ""permissionMode"": ""auto""
}";

        _mockFileService.SetFileExists(true);
        _mockFileService.SetFileContent(json);

        var settings = await _service.GetSettings("test-env", CancellationToken.None);

        Assert.Equal("", settings.Effort);
        Assert.Equal("auto", settings.PermissionMode);
        Assert.Equal("", settings.AllowedTools);
        Assert.False(settings.Debug);
    }

    [Fact]
    public async Task SaveSettings_WritesSupportedValues_ToJson()
    {
        _mockFileService.SetFileExists(false);

        var settings = new ClaudeSettingsDto
        {
            Effort = "xhigh",
            NoSessionPersistence = true,
            PermissionMode = "dontAsk",
            SystemPrompt = "You are a Python expert",
            AllowDangerouslySkipPermissions = true,
            DangerouslyLoadDevelopmentChannels = "server:webhook",
            DangerouslySkipPermissions = true,
            AllowedTools = "Bash(git log *)\nRead",
            AppendSystemPrompt = "Always use TypeScript",
            Bare = true,
            Betas = "interleaved-thinking",
            Channels = "plugin:my-notifier@my-marketplace",
            Debug = true,
            DebugFilter = "api,mcp"
        };

        await _service.SaveSettings("test-env", settings, CancellationToken.None);

        var writtenContent = _mockFileService.GetWrittenContent();
        Assert.Contains("\"effort\": \"xhigh\"", writtenContent);
        Assert.Contains("\"noSessionPersistence\": true", writtenContent);
        Assert.Contains("\"permissionMode\": \"dontAsk\"", writtenContent);
        Assert.Contains("\"systemPrompt\": \"You are a Python expert\"", writtenContent);
        Assert.Contains("\"allowDangerouslySkipPermissions\": true", writtenContent);
        Assert.Contains("\"dangerouslyLoadDevelopmentChannels\": \"server:webhook\"", writtenContent);
        Assert.Contains("\"dangerouslySkipPermissions\": true", writtenContent);
        Assert.Contains("\"allowedTools\": \"Bash(git log *)\\nRead\"", writtenContent);
        Assert.Contains("\"appendSystemPrompt\": \"Always use TypeScript\"", writtenContent);
        Assert.Contains("\"bare\": true", writtenContent);
        Assert.Contains("\"betas\": \"interleaved-thinking\"", writtenContent);
        Assert.Contains("\"channels\": \"plugin:my-notifier@my-marketplace\"", writtenContent);
        Assert.Contains("\"debug\": true", writtenContent);
        Assert.Contains("\"debugFilter\": \"api,mcp\"", writtenContent);
    }

    [Fact]
    public async Task SaveSettings_PreservesUnknownAndUnmappedLegacyFields()
    {
        var existingJson = @"{
    ""permissions"": { ""allow"": [""Read""] },
    ""model"": ""old-model"",
    ""disallowedTools"": ""Bash"",
    ""verbose"": true,
    ""customField"": ""preserved""
}";

        _mockFileService.SetFileExists(true);
        _mockFileService.SetFileContent(existingJson);

        var settings = new ClaudeSettingsDto
        {
            Effort = "high",
            PermissionMode = "plan",
            AllowedTools = "Read"
        };

        await _service.SaveSettings("test-env", settings, CancellationToken.None);

        var writtenContent = _mockFileService.GetWrittenContent();
        Assert.Contains("\"permissions\"", writtenContent);
        Assert.Contains("\"customField\": \"preserved\"", writtenContent);
        Assert.Contains("\"effort\": \"high\"", writtenContent);
        Assert.Contains("\"permissionMode\": \"plan\"", writtenContent);
        Assert.Contains("\"allowedTools\": \"Read\"", writtenContent);
        // Legacy fields with no VibeRails mapping are left alone — user's manual edits stick.
        Assert.Contains("\"model\": \"old-model\"", writtenContent);
        Assert.Contains("\"disallowedTools\": \"Bash\"", writtenContent);
        Assert.Contains("\"verbose\": true", writtenContent);
    }

    [Fact]
    public async Task SaveSettings_ClearsLegacySkipPermissions_WhenItShadowsTheNewKey()
    {
        // skipPermissions is a legacy alias for dangerouslySkipPermissions; GetSettings
        // falls back to it. Without clearing it on save, the legacy value would shadow
        // the user's new choice on the next read.
        var existingJson = @"{
    ""skipPermissions"": true
}";

        _mockFileService.SetFileExists(true);
        _mockFileService.SetFileContent(existingJson);

        var settings = new ClaudeSettingsDto
        {
            DangerouslySkipPermissions = false
        };

        await _service.SaveSettings("test-env", settings, CancellationToken.None);

        var writtenContent = _mockFileService.GetWrittenContent();
        Assert.DoesNotContain("\"skipPermissions\"", writtenContent);
    }

    [Fact]
    public async Task SaveSettings_RemovesEmptyAndDefaultValues()
    {
        var existingJson = @"{
    ""effort"": ""high"",
    ""permissionMode"": ""plan"",
    ""systemPrompt"": ""old"",
    ""debug"": true,
    ""debugFilter"": ""api""
}";

        _mockFileService.SetFileExists(true);
        _mockFileService.SetFileContent(existingJson);

        var settings = new ClaudeSettingsDto
        {
            PermissionMode = "default"
        };

        await _service.SaveSettings("test-env", settings, CancellationToken.None);

        var writtenContent = _mockFileService.GetWrittenContent();
        Assert.DoesNotContain("\"effort\"", writtenContent);
        Assert.DoesNotContain("\"permissionMode\"", writtenContent);
        Assert.DoesNotContain("\"systemPrompt\"", writtenContent);
        Assert.DoesNotContain("\"debug\"", writtenContent);
        Assert.DoesNotContain("\"debugFilter\"", writtenContent);
    }

    private class MockFileService : IFileService
    {
        private bool _fileExists;
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

        public Task<(bool inGet, string projectRoot)> TryGetProjectRootPathAsync(string? workingDirectory = null, CancellationToken cancellationToken = default)
            => Task.FromResult((false, ""));
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
}
