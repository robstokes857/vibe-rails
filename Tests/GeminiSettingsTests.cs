using Moq;
using VibeRails.DB;
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

    public GeminiSettingsTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"GeminiSettingsTests_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDirectory);

        // Set up environment variable for test
        Environment.SetEnvironmentVariable("VIBE_CONTROL_ENVPATH", _testDirectory);

        _mockFileService = new MockFileService();

        _service = new GeminiLlmCliEnvironment(_mockFileService);
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
        Assert.False(settings.VimMode);
        Assert.True(settings.CheckForUpdates);
        Assert.False(settings.YoloMode);
    }

    [Fact]
    public async Task GetSettings_ReadsManagedValues_FromValidJson()
    {
        // Permission posture (defaultApprovalMode) is YOLO-or-nothing via CustomArgs, so it
        // is not read into the DTO.
        var json = @"{
            ""theme"": ""Dark"",
            ""general"": {
                ""vimMode"": true,
                ""enableAutoUpdate"": false,
                ""defaultApprovalMode"": ""auto_edit""
            },
            ""tools"": {
                ""sandbox"": false
            }
        }";

        _mockFileService.SetFileExists(true);
        _mockFileService.SetFileContent(json);

        var settings = await _service.GetSettings("test-env", CancellationToken.None);

        Assert.Equal("Dark", settings.Theme);
        Assert.False(settings.SandboxEnabled);
        Assert.True(settings.VimMode);
        Assert.False(settings.CheckForUpdates);
        Assert.False(settings.YoloMode);
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
        Assert.False(settings.VimMode); // Default
        Assert.True(settings.CheckForUpdates); // Default
        Assert.False(settings.YoloMode); // Default
    }

    [Fact]
    public async Task GetSettings_IgnoresOldCompatibilityFields()
    {
        var json = @"{
            ""general"": {},
            ""sandbox"": {
                ""enabled"": false
            },
            ""checkForUpdates"": false,
            ""tools"": {
                ""autoAccept"": true
            }
        }";

        _mockFileService.SetFileExists(true);
        _mockFileService.SetFileContent(json);

        var settings = await _service.GetSettings("test-env", CancellationToken.None);

        Assert.Equal("Default", settings.Theme);
        Assert.True(settings.SandboxEnabled);
        Assert.True(settings.CheckForUpdates);
        Assert.False(settings.VimMode); // general.vimMode not present
    }

    [Fact]
    public async Task GetSettings_IgnoresApprovalMode()
    {
        // defaultApprovalMode is no longer read: YOLO is a launch flag, not a DTO field.
        var json = @"{ ""general"": { ""defaultApprovalMode"": ""yolo"" } }";
        _mockFileService.SetFileExists(true);
        _mockFileService.SetFileContent(json);

        var settings = await _service.GetSettings("test-env", CancellationToken.None);

        Assert.False(settings.YoloMode);
    }

    // ===========================================
    // SaveSettings Tests
    // ===========================================

    [Fact]
    public async Task SaveSettings_WritesManagedValues_ToJson()
    {
        _mockFileService.SetFileExists(false);

        var settings = new GeminiSettingsDto
        {
            Theme = "Dark",
            SandboxEnabled = false,
            VimMode = true,
            CheckForUpdates = false,
            YoloMode = false
        };

        await _service.SaveSettings("test-env", settings, CancellationToken.None);

        var writtenJson = _mockFileService.GetWrittenContent();
        Assert.DoesNotContain("\"theme\":", writtenJson); // VibeRails does not write theme
        Assert.Contains("\"enableAutoUpdate\": false", writtenJson);
        Assert.Contains("\"vimMode\": true", writtenJson);
        Assert.Contains("\"sandbox\": false", writtenJson); // tools.sandbox
        // Permission posture is YOLO-or-nothing via CustomArgs; never persisted here.
        Assert.DoesNotContain("\"defaultApprovalMode\"", writtenJson);
        Assert.DoesNotContain("\"disableYoloMode\"", writtenJson);
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
        Assert.Contains("\"theme\": \"Dark\"", writtenJson); // Preserved; VibeRails no longer manages Gemini theme
        Assert.Contains("\"selectedAuthType\": \"oauth-personal\"", writtenJson); // Preserved
        Assert.Contains("\"customField\": \"preserved\"", writtenJson); // Preserved
    }

    [Fact]
    public async Task SaveSettings_RemovesOldCompatibilityFields()
    {
        var existingJson = @"{
            ""checkForUpdates"": false,
            ""sandbox"": {
                ""enabled"": false
            },
            ""tools"": {
                ""autoAccept"": true
            }
        }";

        _mockFileService.SetFileExists(true);
        _mockFileService.SetFileContent(existingJson);

        await _service.SaveSettings("test-env", new GeminiSettingsDto(), CancellationToken.None);

        var writtenJson = _mockFileService.GetWrittenContent();
        Assert.DoesNotContain("\"checkForUpdates\"", writtenJson);
        Assert.DoesNotContain("\"enabled\"", writtenJson);
        Assert.DoesNotContain("\"autoAccept\"", writtenJson);
        Assert.Contains("\"enableAutoUpdate\": true", writtenJson);
        Assert.Contains("\"sandbox\": true", writtenJson);
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
        Assert.Contains("\"tools\":", writtenJson);
        Assert.DoesNotContain("\"security\":", writtenJson);
    }

    [Fact]
    public async Task SaveSettings_LeavesUserApprovalModeUntouched()
    {
        // Regression guard: VibeRails must not edit Gemini's defaultApprovalMode — even a
        // YOLO save (YOLO is a launch flag) leaves any user-set approval mode alone.
        var existingJson = @"{
            ""general"": {
                ""defaultApprovalMode"": ""auto_edit""
            }
        }";

        _mockFileService.SetFileExists(true);
        _mockFileService.SetFileContent(existingJson);

        await _service.SaveSettings("test-env", new GeminiSettingsDto { YoloMode = true }, CancellationToken.None);

        var writtenJson = _mockFileService.GetWrittenContent();
        Assert.Contains("\"defaultApprovalMode\": \"auto_edit\"", writtenJson);
        Assert.DoesNotContain("\"disableYoloMode\"", writtenJson);
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
