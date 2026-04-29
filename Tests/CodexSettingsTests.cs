using VibeRails.DTOs;
using VibeRails.Interfaces;
using VibeRails.Services.LlmClis;
using Xunit;

namespace Tests;

public class CodexSettingsTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly CodexLlmCliEnvironment _service;
    private readonly MockFileService _mockFileService;

    public CodexSettingsTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"CodexSettingsTests_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDirectory);

        Environment.SetEnvironmentVariable("VIBE_CONTROL_ENVPATH", _testDirectory);

        _mockFileService = new MockFileService();
        _service = new CodexLlmCliEnvironment(_mockFileService);
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

        Assert.Equal("untrusted", settings.AskForApproval);
        Assert.False(settings.Yolo);
        Assert.False(settings.FullAuto);
        Assert.False(settings.NoAltScreen);
        Assert.False(settings.Oss);
        Assert.Equal("", settings.Prompt);
    }

    [Fact]
    public async Task GetSettings_ReadsAllSupportedValues_FromToml()
    {
        var toml = @"
ask_for_approval = ""on-request""
yolo = true
full_auto = true
no_alt_screen = true
oss = true
prompt = ""Investigate failing tests""
";

        _mockFileService.SetFileExists(true);
        _mockFileService.SetFileContent(toml);

        var settings = await _service.GetSettings("test-env", CancellationToken.None);

        Assert.Equal("on-request", settings.AskForApproval);
        Assert.True(settings.Yolo);
        Assert.True(settings.FullAuto);
        Assert.True(settings.NoAltScreen);
        Assert.True(settings.Oss);
        Assert.Equal("Investigate failing tests", settings.Prompt);
    }

    [Fact]
    public async Task GetSettings_FallsBackFromLegacyApproval_AndNormalizesDeprecatedOnFailure()
    {
        var toml = @"approval = 'on-failure'";

        _mockFileService.SetFileExists(true);
        _mockFileService.SetFileContent(toml);

        var settings = await _service.GetSettings("test-env", CancellationToken.None);

        Assert.Equal("on-request", settings.AskForApproval);
    }

    [Fact]
    public async Task GetSettings_ReturnsDefaults_ForMissingFields()
    {
        _mockFileService.SetFileExists(true);
        _mockFileService.SetFileContent(@"prompt = ""Start here""");

        var settings = await _service.GetSettings("test-env", CancellationToken.None);

        Assert.Equal("untrusted", settings.AskForApproval);
        Assert.False(settings.Yolo);
        Assert.False(settings.FullAuto);
        Assert.False(settings.NoAltScreen);
        Assert.False(settings.Oss);
        Assert.Equal("Start here", settings.Prompt);
    }

    [Fact]
    public async Task SaveSettings_WritesSupportedValues_ToToml()
    {
        _mockFileService.SetFileExists(false);

        var settings = new CodexSettingsDto
        {
            AskForApproval = "on-request",
            Yolo = true,
            FullAuto = true,
            NoAltScreen = true,
            Oss = true,
            Prompt = "Investigate failing tests"
        };

        await _service.SaveSettings("test-env", settings, CancellationToken.None);

        var writtenContent = _mockFileService.GetWrittenContent();
        Assert.Contains("ask_for_approval = \"on-request\"", writtenContent);
        Assert.Contains("yolo = true", writtenContent);
        Assert.Contains("full_auto = true", writtenContent);
        Assert.Contains("no_alt_screen = true", writtenContent);
        Assert.Contains("oss = true", writtenContent);
        Assert.Contains("prompt = \"Investigate failing tests\"", writtenContent);
    }

    [Fact]
    public async Task SaveSettings_PreservesUnmappedLegacyFields_ButClearsAliasedApproval()
    {
        var existingToml = @"# Codex CLI Configuration
# Custom comment

model = ""old-model""
sandbox = ""read-only""
approval = ""on-failure""
search = true
custom_field = ""preserved""
";

        _mockFileService.SetFileExists(true);
        _mockFileService.SetFileContent(existingToml);

        var settings = new CodexSettingsDto
        {
            AskForApproval = "never",
            FullAuto = true
        };

        await _service.SaveSettings("test-env", settings, CancellationToken.None);

        var writtenContent = _mockFileService.GetWrittenContent();
        Assert.Contains("# Custom comment", writtenContent);
        Assert.Contains("custom_field = \"preserved\"", writtenContent);
        Assert.Contains("ask_for_approval = \"never\"", writtenContent);
        Assert.Contains("full_auto = true", writtenContent);
        // `approval` is the legacy alias for `ask_for_approval` and must be cleared
        // so it doesn't shadow the new key on read.
        Assert.DoesNotMatch(@"(?m)^\s*approval\s*=", writtenContent);
        // Other legacy fields without VibeRails mapping are left alone.
        Assert.Contains("model = \"old-model\"", writtenContent);
        Assert.Contains("sandbox = \"read-only\"", writtenContent);
        Assert.Contains("search = true", writtenContent);
    }

    [Fact]
    public async Task SaveSettings_EscapesPromptWithSpecialCharacters_AndRoundTrips()
    {
        _mockFileService.SetFileExists(false);

        var dangerous = "She said \"hi\"\nyolo = true\n# pwned\\nope";
        var settings = new CodexSettingsDto
        {
            AskForApproval = "untrusted",
            Prompt = dangerous
        };

        await _service.SaveSettings("test-env", settings, CancellationToken.None);

        var written = _mockFileService.GetWrittenContent();
        // The user's literal text must not appear unescaped — that would let
        // a quote/newline break out of the prompt = "..." value and inject keys.
        Assert.DoesNotContain("She said \"hi\"\nyolo = true", written);
        // And the embedded `yolo = true` line must not have actually been
        // promoted to a TOML key by our writer.
        Assert.DoesNotMatch(@"(?m)^\s*yolo\s*=\s*true\s*$", written);

        // Round-trip the written content back through GetSettings.
        _mockFileService.SetFileExists(true);
        _mockFileService.SetFileContent(written);

        var roundTripped = await _service.GetSettings("test-env", CancellationToken.None);
        Assert.Equal(dangerous, roundTripped.Prompt);
        Assert.False(roundTripped.Yolo);
    }

    [Fact]
    public async Task SaveSettings_RemovesEmptyPrompt()
    {
        var existingToml = @"
ask_for_approval = ""on-request""
prompt = ""old prompt""
";

        _mockFileService.SetFileExists(true);
        _mockFileService.SetFileContent(existingToml);

        var settings = new CodexSettingsDto
        {
            AskForApproval = "on-request",
            Prompt = ""
        };

        await _service.SaveSettings("test-env", settings, CancellationToken.None);

        var writtenContent = _mockFileService.GetWrittenContent();
        Assert.DoesNotContain("prompt =", writtenContent);
        Assert.Contains("ask_for_approval = \"on-request\"", writtenContent);
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
