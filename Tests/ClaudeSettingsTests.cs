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
        Assert.Equal("", settings.Model);
        Assert.False(settings.FastMode);
        Assert.False(settings.DangerouslySkipPermissions);
        Assert.False(settings.NoSessionPersistence);
        Assert.Equal("", settings.SystemPrompt);
        Assert.False(settings.Bare);
        Assert.False(settings.Debug);
    }

    [Fact]
    public async Task GetSettings_ReadsEffortLevel_FromValidJson()
    {
        // effortLevel is the only settings-file key VibeRails reads. The permissions block
        // is intentionally ignored (permission posture is YOLO-or-nothing via CustomArgs).
        var json = @"{
    ""effortLevel"": ""high"",
    ""permissions"": {
        ""defaultMode"": ""plan"",
        ""allow"": [
            ""Bash(git log *)"",
            ""Read""
        ]
    }
}";

        _mockFileService.SetFileExists(true);
        _mockFileService.SetFileContent(json);

        var settings = await _service.GetSettings("test-env", CancellationToken.None);

        Assert.Equal("high", settings.Effort);
        Assert.False(settings.DangerouslySkipPermissions);
        Assert.False(settings.NoSessionPersistence);
    }

    [Theory]
    [InlineData("low")]
    [InlineData("medium")]
    [InlineData("high")]
    [InlineData("xhigh")]
    public async Task GetSettings_AcceptsAllEffortLevels(string level)
    {
        _mockFileService.SetFileExists(true);
        _mockFileService.SetFileContent($@"{{ ""effortLevel"": ""{level}"" }}");

        var settings = await _service.GetSettings("test-env", CancellationToken.None);

        Assert.Equal(level, settings.Effort);
    }

    [Fact]
    public async Task GetSettings_IgnoresMaxEffort_SessionOnlyValue()
    {
        // Upstream `effortLevel` never holds "max" (session-only; rejected in settings files).
        // A stale "max" from an older VibeRails build reads as empty; the edit modal recovers
        // the real value from the --effort launch flag in CustomArgs.
        _mockFileService.SetFileExists(true);
        _mockFileService.SetFileContent(@"{ ""effortLevel"": ""max"" }");

        var settings = await _service.GetSettings("test-env", CancellationToken.None);

        Assert.Equal("", settings.Effort);
    }

    [Fact]
    public async Task SaveSettings_MaxEffort_RemovesEffortLevelKey()
    {
        // "max" is launch-flag-only; saving it must strip effortLevel from settings.json
        // rather than persist a value Claude rejects there.
        _mockFileService.SetFileExists(true);
        _mockFileService.SetFileContent(@"{ ""effortLevel"": ""max"" }");

        await _service.SaveSettings("test-env", new ClaudeSettingsDto { Effort = "max" }, CancellationToken.None);

        var writtenContent = _mockFileService.GetWrittenContent();
        Assert.DoesNotContain("effortLevel", writtenContent);
    }

    [Fact]
    public async Task GetSettings_IgnoresOldVibeRailsCompatibilityKeys()
    {
        var json = @"{
    ""effort"": ""high"",
    ""permissionMode"": ""auto"",
    ""allowedTools"": ""Read"",
    ""skipPermissions"": true
}";

        _mockFileService.SetFileExists(true);
        _mockFileService.SetFileContent(json);

        var settings = await _service.GetSettings("test-env", CancellationToken.None);

        Assert.Equal("", settings.Effort);
        Assert.False(settings.DangerouslySkipPermissions);
    }

    [Fact]
    public async Task SaveSettings_WritesEffortLevel_ToJson()
    {
        _mockFileService.SetFileExists(false);

        var settings = new ClaudeSettingsDto { Effort = "xhigh" };

        await _service.SaveSettings("test-env", settings, CancellationToken.None);

        var writtenContent = _mockFileService.GetWrittenContent();
        Assert.Contains("\"effortLevel\": \"xhigh\"", writtenContent);
    }

    [Fact]
    public async Task SaveSettings_LeavesUserPermissionsBlockUntouched()
    {
        // Regression guard: VibeRails must never edit Claude's permissions block. A user's
        // curated allow/deny lists and defaultMode survive a save even though the UI cannot
        // set them — permission posture is YOLO-or-nothing via CustomArgs launch flags.
        var existingJson = @"{
    ""effortLevel"": ""low"",
    ""permissions"": {
        ""allow"": [""Bash(git log *)"", ""Read""],
        ""deny"": [""WebFetch""],
        ""defaultMode"": ""acceptEdits""
    }
}";

        _mockFileService.SetFileExists(true);
        _mockFileService.SetFileContent(existingJson);

        // Even a DTO that flips YOLO on must not rewrite the permissions block.
        var settings = new ClaudeSettingsDto { Effort = "high", DangerouslySkipPermissions = true };

        await _service.SaveSettings("test-env", settings, CancellationToken.None);

        var writtenContent = _mockFileService.GetWrittenContent();
        Assert.Contains("\"effortLevel\": \"high\"", writtenContent);
        Assert.Contains("\"allow\"", writtenContent);
        Assert.Contains("\"Bash(git log *)\"", writtenContent);
        Assert.Contains("\"deny\"", writtenContent);
        Assert.Contains("\"WebFetch\"", writtenContent);
        Assert.Contains("\"defaultMode\": \"acceptEdits\"", writtenContent);
        // YOLO is a launch flag, not a settings-file key.
        Assert.DoesNotContain("\"dangerouslySkipPermissions\"", writtenContent);
    }

    [Fact]
    public async Task SaveSettings_PreservesUnknownFields_ButRemovesStaleTopLevelKeys()
    {
        var existingJson = @"{
    ""model"": ""old-model"",
    ""effort"": ""medium"",
    ""permissionMode"": ""plan"",
    ""allowedTools"": ""Read"",
    ""systemPrompt"": ""old"",
    ""debug"": true,
    ""fastModePerSessionOptIn"": true,
    ""customField"": ""preserved""
}";

        _mockFileService.SetFileExists(true);
        _mockFileService.SetFileContent(existingJson);

        var settings = new ClaudeSettingsDto { Effort = "high" };

        await _service.SaveSettings("test-env", settings, CancellationToken.None);

        var writtenContent = _mockFileService.GetWrittenContent();
        Assert.Contains("\"customField\": \"preserved\"", writtenContent);
        // fastModePerSessionOptIn is an admin/managed key VibeRails does not own — preserved.
        Assert.Contains("\"fastModePerSessionOptIn\": true", writtenContent);
        Assert.Contains("\"effortLevel\": \"high\"", writtenContent);
        // model is now a VibeRails-managed key: an empty DTO removes it on save.
        Assert.DoesNotContain("\"model\"", writtenContent);
        // Stale top-level keys older VibeRails builds wrote are cleaned up.
        Assert.DoesNotContain("\"effort\":", writtenContent);
        Assert.DoesNotContain("\"permissionMode\"", writtenContent);
        Assert.DoesNotContain("\"allowedTools\"", writtenContent);
        Assert.DoesNotContain("\"systemPrompt\"", writtenContent);
        Assert.DoesNotContain("\"debug\"", writtenContent);
    }

    [Fact]
    public async Task SaveSettings_RemovesEffortLevel_WhenEmpty()
    {
        var existingJson = @"{
    ""effortLevel"": ""high""
}";

        _mockFileService.SetFileExists(true);
        _mockFileService.SetFileContent(existingJson);

        await _service.SaveSettings("test-env", new ClaudeSettingsDto(), CancellationToken.None);

        var writtenContent = _mockFileService.GetWrittenContent();
        Assert.DoesNotContain("\"effortLevel\"", writtenContent);
    }

    [Fact]
    public async Task GetSettings_ReadsModelAndFastMode_FromValidJson()
    {
        var json = @"{
    ""effortLevel"": ""high"",
    ""model"": ""claude-opus-4-8"",
    ""fastMode"": true
}";

        _mockFileService.SetFileExists(true);
        _mockFileService.SetFileContent(json);

        var settings = await _service.GetSettings("test-env", CancellationToken.None);

        Assert.Equal("high", settings.Effort);
        Assert.Equal("claude-opus-4-8", settings.Model);
        Assert.True(settings.FastMode);
    }

    [Fact]
    public async Task SaveSettings_WritesModelAndFastMode_ToJson()
    {
        _mockFileService.SetFileExists(false);

        var settings = new ClaudeSettingsDto { Effort = "xhigh", Model = "claude-opus-4-8", FastMode = true };

        await _service.SaveSettings("test-env", settings, CancellationToken.None);

        var writtenContent = _mockFileService.GetWrittenContent();
        Assert.Contains("\"effortLevel\": \"xhigh\"", writtenContent);
        Assert.Contains("\"model\": \"claude-opus-4-8\"", writtenContent);
        // fastMode is a settings-file-only key (no --fast launch flag exists).
        Assert.Contains("\"fastMode\": true", writtenContent);
    }

    [Fact]
    public async Task SaveSettings_RemovesModelAndFastMode_WhenDefault()
    {
        var existingJson = @"{
    ""model"": ""claude-opus-4-8"",
    ""fastMode"": true
}";

        _mockFileService.SetFileExists(true);
        _mockFileService.SetFileContent(existingJson);

        await _service.SaveSettings("test-env", new ClaudeSettingsDto(), CancellationToken.None);

        var writtenContent = _mockFileService.GetWrittenContent();
        Assert.DoesNotContain("\"model\"", writtenContent);
        Assert.DoesNotContain("\"fastMode\"", writtenContent);
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
