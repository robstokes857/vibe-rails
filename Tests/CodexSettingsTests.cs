using VibeRails.DTOs;
using VibeRails.Interfaces;
using VibeRails.Services.LlmClis;
using VibeRails.Utils;
using Xunit;

namespace Tests;

[Collection("ProcessEnvIsolation")]
public class CodexSettingsTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly string _previousEnvPath;
    private readonly CodexLlmCliEnvironment _service;
    private readonly MockFileService _mockFileService;

    public CodexSettingsTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"CodexSettingsTests_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDirectory);

        _previousEnvPath = ParserConfigs.GetEnvPath();
        ParserConfigs.SetEnvPath(_testDirectory);

        _mockFileService = new MockFileService();
        _service = new CodexLlmCliEnvironment(_mockFileService);
    }

    public void Dispose()
    {
        ParserConfigs.SetEnvPath(_previousEnvPath);
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

        Assert.False(settings.Yolo);
        Assert.False(settings.NoAltScreen);
        Assert.Equal("", settings.Model);
        Assert.Equal("", settings.Effort);
        Assert.False(settings.FastMode);
    }

    [Fact]
    public async Task SettingsOperations_RejectEnvironmentPathTraversal()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _service.GetSettings("../../outside", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => _service.SaveSettings("../../outside", new CodexSettingsDto(), CancellationToken.None));
    }

    [Fact]
    public async Task GetSettings_ReadsCurrentValues_FromToml()
    {
        var toml = @"
model = ""gpt-5.6-sol""
model_reasoning_effort = ""xhigh""
service_tier = ""fast""

[features]
fast_mode = true

[tui]
alternate_screen = ""never""
";

        _mockFileService.SetFileExists(true);
        _mockFileService.SetFileContent(toml);

        var settings = await _service.GetSettings("test-env", CancellationToken.None);

        Assert.Equal("gpt-5.6-sol", settings.Model);
        Assert.Equal("xhigh", settings.Effort);
        Assert.True(settings.FastMode);
        Assert.True(settings.NoAltScreen);
    }

    [Theory]
    [InlineData("max")]
    [InlineData("ultra")]
    public async Task GetSettings_ReadsCurrentHighestReasoningEfforts_FromToml(string effort)
    {
        var toml = $@"
model = ""gpt-5.6-sol""
model_reasoning_effort = ""{effort}""
";

        _mockFileService.SetFileExists(true);
        _mockFileService.SetFileContent(toml);

        var settings = await _service.GetSettings("test-env", CancellationToken.None);

        Assert.Equal(effort, settings.Effort);
    }

    [Fact]
    public async Task GetSettings_IgnoresApprovalAndSandboxKeys()
    {
        // Permission posture is YOLO-or-nothing via CustomArgs: GetSettings must not derive
        // Yolo (or anything) from approval_policy / sandbox_mode / model_provider.
        var toml = @"
approval_policy = ""never""
sandbox_mode = ""danger-full-access""
model_provider = ""oss""
model = ""gpt-5.6-terra""
";

        _mockFileService.SetFileExists(true);
        _mockFileService.SetFileContent(toml);

        var settings = await _service.GetSettings("test-env", CancellationToken.None);

        Assert.False(settings.Yolo);
        Assert.Equal("gpt-5.6-terra", settings.Model);
    }

    [Fact]
    public async Task SaveSettings_WritesCurrentValues_ToToml()
    {
        _mockFileService.SetFileExists(false);

        var settings = new CodexSettingsDto
        {
            NoAltScreen = true,
            Model = "gpt-5.6-luna",
            Effort = "xhigh",
            FastMode = true
        };

        await _service.SaveSettings("test-env", settings, CancellationToken.None);

        var writtenContent = _mockFileService.GetWrittenContent();
        Assert.Contains("model = \"gpt-5.6-luna\"", writtenContent);
        Assert.Contains("model_reasoning_effort = \"xhigh\"", writtenContent);
        Assert.Contains("service_tier = \"fast\"", writtenContent);
        Assert.Contains("[features]", writtenContent);
        Assert.Contains("fast_mode = true", writtenContent);
        Assert.Contains("[tui]", writtenContent);
        Assert.Contains("alternate_screen = \"never\"", writtenContent);

        // Permission posture is YOLO-or-nothing via CustomArgs; never written to config.toml.
        Assert.DoesNotContain("approval_policy =", writtenContent);
        Assert.DoesNotContain("sandbox_mode =", writtenContent);
        Assert.DoesNotContain("model_provider =", writtenContent);
    }

    [Theory]
    [InlineData("max")]
    [InlineData("ultra")]
    public async Task SaveSettings_WritesCurrentHighestReasoningEfforts_ToToml(string effort)
    {
        _mockFileService.SetFileExists(false);

        await _service.SaveSettings("test-env", new CodexSettingsDto
        {
            Model = "gpt-5.6-sol",
            Effort = effort
        }, CancellationToken.None);

        var writtenContent = _mockFileService.GetWrittenContent();
        Assert.Contains($"model_reasoning_effort = \"{effort}\"", writtenContent);
    }

    [Fact]
    public async Task SaveSettings_LeavesUserApprovalAndSandboxUntouched()
    {
        // Regression guard: VibeRails must not rewrite the user's approval_policy /
        // sandbox_mode / model_provider — even a YOLO save persists nothing there
        // (YOLO is a launch flag), so any hand-set values survive.
        var existingToml = @"
approval_policy = ""never""
sandbox_mode = ""read-only""
model_provider = ""azure""
model = ""gpt-5.6-sol""
";

        _mockFileService.SetFileExists(true);
        _mockFileService.SetFileContent(existingToml);

        await _service.SaveSettings("test-env", new CodexSettingsDto
        {
            Yolo = true,
            Model = "gpt-5.6-sol"
        }, CancellationToken.None);

        var writtenContent = _mockFileService.GetWrittenContent();
        Assert.Contains("approval_policy = \"never\"", writtenContent);
        Assert.Contains("sandbox_mode = \"read-only\"", writtenContent);
        Assert.Contains("model_provider = \"azure\"", writtenContent);
        Assert.Contains("model = \"gpt-5.6-sol\"", writtenContent);
    }

    [Fact]
    public async Task SaveSettings_DoesNotRewriteBareGpt5Alias()
    {
        // Bare gpt-5 used to select gpt-5.4. Once that pinned model was removed,
        // the compatibility policy requires preserving the explicit value as-is.
        _mockFileService.SetFileExists(false);

        await _service.SaveSettings("test-env", new CodexSettingsDto
        {
            Model = "gpt-5",
            Effort = "high"
        }, CancellationToken.None);

        var writtenContent = _mockFileService.GetWrittenContent();
        Assert.Contains("model = \"gpt-5\"", writtenContent);
        Assert.Contains("model_reasoning_effort = \"high\"", writtenContent);
    }

    [Fact]
    public async Task SaveSettings_DoesNotRewriteRetiredCodexAliases()
    {
        // "gpt-5-codex" used to be rewritten to gpt-5.3-codex, which Codex has since retired.
        // Per the compatibility policy the alias passes through unchanged (the env is already
        // broken upstream; users delete/recreate rather than VibeRails carrying migration code).
        _mockFileService.SetFileExists(false);

        await _service.SaveSettings("test-env", new CodexSettingsDto { Model = "gpt-5-codex" }, CancellationToken.None);

        var writtenContent = _mockFileService.GetWrittenContent();
        Assert.Contains("model = \"gpt-5-codex\"", writtenContent);
    }

    [Fact]
    public async Task SaveSettings_InsertsRootKeysBeforeExistingTables()
    {
        var existingToml = """
            # Codex CLI Configuration

            [features]
            fast_mode = false
            """;

        _mockFileService.SetFileExists(true);
        _mockFileService.SetFileContent(existingToml);

        await _service.SaveSettings("test-env", new CodexSettingsDto
        {
            Model = "gpt-5.6-luna",
            FastMode = true
        }, CancellationToken.None);

        var writtenContent = _mockFileService.GetWrittenContent();
        Assert.Matches(@"(?s)model = ""gpt-5\.6-luna"".*\[features\]", writtenContent);
        Assert.Matches(@"(?s)service_tier = ""fast"".*\[features\]", writtenContent);
        Assert.Contains("fast_mode = true", writtenContent);
    }

    [Fact]
    public async Task SaveSettings_PreservesUnknownFields_ButRemovesLegacyAliases()
    {
        var existingToml = @"# Codex CLI Configuration
# Custom comment

model = ""old-model""
approval = ""on-failure""
yolo = true
full_auto = true
no_alt_screen = true
oss = true
prompt = ""old prompt""
search = true
custom_field = ""preserved""
";

        _mockFileService.SetFileExists(true);
        _mockFileService.SetFileContent(existingToml);

        await _service.SaveSettings("test-env", new CodexSettingsDto { Model = "gpt-5.5" }, CancellationToken.None);

        var writtenContent = _mockFileService.GetWrittenContent();
        Assert.Contains("# Custom comment", writtenContent);
        Assert.Contains("custom_field = \"preserved\"", writtenContent);
        Assert.Contains("search = true", writtenContent);
        Assert.Contains("model = \"gpt-5.5\"", writtenContent);
        // Legacy VibeRails-managed alias keys are stripped on save.
        Assert.DoesNotMatch(@"(?m)^\s*ask_for_approval\s*=", writtenContent);
        Assert.DoesNotMatch(@"(?m)^\s*approval\s*=", writtenContent);
        Assert.DoesNotMatch(@"(?m)^\s*yolo\s*=", writtenContent);
        Assert.DoesNotMatch(@"(?m)^\s*full_auto\s*=", writtenContent);
        Assert.DoesNotMatch(@"(?m)^\s*no_alt_screen\s*=", writtenContent);
        Assert.DoesNotMatch(@"(?m)^\s*oss\s*=", writtenContent);
        Assert.DoesNotMatch(@"(?m)^\s*prompt\s*=", writtenContent);
    }

    [Fact]
    public async Task SaveSettings_RemovesManagedValues_WhenDefault_ButKeepsApprovalAndSandbox()
    {
        var existingToml = @"
approval_policy = ""on-request""
sandbox_mode = ""workspace-write""
model_provider = ""oss""
model = ""gpt-5.6-sol""
model_reasoning_effort = ""high""
service_tier = ""fast""

[features]
fast_mode = true

[tui]
alternate_screen = ""never""
";

        _mockFileService.SetFileExists(true);
        _mockFileService.SetFileContent(existingToml);

        await _service.SaveSettings("test-env", new CodexSettingsDto(), CancellationToken.None);

        var writtenContent = _mockFileService.GetWrittenContent();
        // VibeRails-managed keys are removed when empty/default...
        Assert.DoesNotMatch(@"(?m)^\s*model\s*=", writtenContent);
        Assert.DoesNotContain("model_reasoning_effort =", writtenContent);
        Assert.DoesNotContain("service_tier =", writtenContent);
        Assert.DoesNotContain("fast_mode =", writtenContent);
        Assert.DoesNotContain("alternate_screen =", writtenContent);
        // ...but the user's permission/provider keys are left exactly as they were.
        Assert.Contains("approval_policy = \"on-request\"", writtenContent);
        Assert.Contains("sandbox_mode = \"workspace-write\"", writtenContent);
        Assert.Contains("model_provider = \"oss\"", writtenContent);
    }

    [Fact]
    public async Task SaveSettings_LeavesKeysInsideNestedTablesUntouched()
    {
        // Regression guard for root-table scoping: managed/legacy keys are edited only in the
        // root table. Same-named keys inside a user's [profiles.*] table must survive a save.
        var existingToml = @"
model = ""gpt-5.4""
prompt = ""legacy root prompt""

[profiles.work]
model = ""gpt-5.3-codex""
prompt = ""Investigate failing tests""
approval_policy = ""never""
";

        _mockFileService.SetFileExists(true);
        _mockFileService.SetFileContent(existingToml);

        // Default DTO clears the ROOT model and strips the legacy root `prompt`.
        await _service.SaveSettings("test-env", new CodexSettingsDto(), CancellationToken.None);

        var written = _mockFileService.GetWrittenContent();

        // The whole [profiles.work] table is preserved verbatim.
        Assert.Contains("[profiles.work]", written);
        Assert.Contains("model = \"gpt-5.3-codex\"", written);
        Assert.Contains("prompt = \"Investigate failing tests\"", written);
        Assert.Contains("approval_policy = \"never\"", written);

        // ...while only the ROOT copies are removed.
        Assert.DoesNotContain("legacy root prompt", written);
        Assert.DoesNotContain("gpt-5.4", written);
    }

    [Fact]
    public async Task GetSettings_ReadsModelFromRootTable_IgnoringNestedTables()
    {
        // A model defined only inside a nested table must not leak into the DTO as the root model.
        var toml = @"
[profiles.work]
model = ""gpt-5.3-codex""
";
        _mockFileService.SetFileExists(true);
        _mockFileService.SetFileContent(toml);

        var settings = await _service.GetSettings("test-env", CancellationToken.None);

        Assert.Equal("", settings.Model);
    }

    [Fact]
    public async Task SaveSettings_DisablingNoAltScreen_RemovesRootDottedForm_AndRoundTrips()
    {
        // The generated default config advertises the dotted form `tui.alternate_screen = "never"`.
        // Disabling must remove it (not only the [tui] section form), or it reappears on next read.
        var existingToml = @"
tui.alternate_screen = ""never""
model = ""gpt-5.6-sol""
";
        _mockFileService.SetFileExists(true);
        _mockFileService.SetFileContent(existingToml);

        await _service.SaveSettings(
            "test-env",
            new CodexSettingsDto { NoAltScreen = false, Model = "gpt-5.6-sol" },
            CancellationToken.None);

        var written = _mockFileService.GetWrittenContent();
        Assert.DoesNotContain("alternate_screen", written);

        _mockFileService.SetFileContent(written);
        var reread = await _service.GetSettings("test-env", CancellationToken.None);
        Assert.False(reread.NoAltScreen);
    }

    [Fact]
    public async Task SaveSettings_EnablingFastMode_WithStaleRootDottedKey_DoesNotDuplicate()
    {
        var existingToml = @"
features.fast_mode = false
model = ""gpt-5.6-terra""
";
        _mockFileService.SetFileExists(true);
        _mockFileService.SetFileContent(existingToml);

        await _service.SaveSettings(
            "test-env",
            new CodexSettingsDto { FastMode = true, Model = "gpt-5.6-terra" },
            CancellationToken.None);

        var written = _mockFileService.GetWrittenContent();

        // The stale root dotted form is gone; only the [features] section form remains (no
        // duplicate-key ambiguity), and it round-trips as enabled.
        Assert.DoesNotMatch(@"(?m)^\s*features\.fast_mode\s*=", written);
        Assert.Contains("[features]", written);

        _mockFileService.SetFileContent(written);
        var reread = await _service.GetSettings("test-env", CancellationToken.None);
        Assert.True(reread.FastMode);
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
        public void MoveDirectory(string sourceDirName, string destDirName) { }
    }
}
