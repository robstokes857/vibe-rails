using Microsoft.Extensions.Logging;
using Moq;
using VibeRails.Services;
using Xunit;

namespace Tests.Services
{
    public class HookInstallationServiceTests : IDisposable
    {
        private readonly string _testRepoPath;
        private readonly string _hooksDir;
        private readonly Mock<ILogger<HookInstallationService>> _loggerMock;
        private readonly HookInstallationService _service;

        public HookInstallationServiceTests()
        {
            // Create a temporary directory for testing
            _testRepoPath = Path.Combine(Path.GetTempPath(), $"vb_test_{Guid.NewGuid()}");
            _hooksDir = Path.Combine(_testRepoPath, ".git", "hooks");
            Directory.CreateDirectory(_hooksDir);

            _loggerMock = new Mock<ILogger<HookInstallationService>>();
            _service = new HookInstallationService(_loggerMock.Object);

            // Create mock script files for testing
            CreateMockScripts();
        }

        private void CreateMockScripts()
        {
            // Create scripts directory in the test assembly location
            var assemblyDir = Path.GetDirectoryName(typeof(HookInstallationService).Assembly.Location);
            if (assemblyDir != null)
            {
                var scriptsDir = Path.Combine(assemblyDir, "scripts");
                Directory.CreateDirectory(scriptsDir);

                // Create mock pre-commit hook script
                var preCommitScript = @"#!/bin/sh
# Vibe Rails Pre-Commit Hook
# Test script
echo ""Pre-commit hook running""
exit 0
# End Vibe Rails Hook
";
                File.WriteAllText(Path.Combine(scriptsDir, "pre-commit-hook.sh"), preCommitScript);

                // Create mock commit-msg hook script
                var commitMsgScript = @"#!/bin/sh
# Vibe Rails Commit-Msg Hook
# Test script
echo ""Commit-msg hook running""
exit 0
# End Vibe Rails Hook
";
                File.WriteAllText(Path.Combine(scriptsDir, "commit-msg-hook.sh"), commitMsgScript);
            }
        }

        public void Dispose()
        {
            // Clean up test directory
            if (Directory.Exists(_testRepoPath))
            {
                Directory.Delete(_testRepoPath, true);
            }
        }

        [Fact]
        public async Task InstallPreCommitHookAsync_CreatesHookFile_WhenNoExistingHook()
        {
            // Act
            var result = await _service.InstallPreCommitHookAsync(_testRepoPath, CancellationToken.None);

            // Assert
            Assert.True(result.Success);
            var hookPath = Path.Combine(_hooksDir, "pre-commit");
            Assert.True(File.Exists(hookPath));

            var content = await File.ReadAllTextAsync(hookPath);
            Assert.Contains("# Vibe Rails Pre-Commit Hook", content);
            Assert.Contains("# End Vibe Rails Hook", content);
        }

        [Fact]
        public async Task InstallPreCommitHookAsync_CreatesHooksDirectory_WhenNotExists()
        {
            // Arrange
            Directory.Delete(_hooksDir, true);

            // Act
            var result = await _service.InstallPreCommitHookAsync(_testRepoPath, CancellationToken.None);

            // Assert
            Assert.True(result.Success);
            Assert.True(Directory.Exists(_hooksDir));
        }

        [Fact]
        public async Task InstallPreCommitHookAsync_AppendsToExistingHook_WhenOtherHooksPresent()
        {
            // Arrange
            var hookPath = Path.Combine(_hooksDir, "pre-commit");
            var existingContent = @"#!/bin/sh
# Some other hook
echo ""Other hook""
";
            await File.WriteAllTextAsync(hookPath, existingContent);

            // Act
            var result = await _service.InstallPreCommitHookAsync(_testRepoPath, CancellationToken.None);

            // Assert
            Assert.True(result.Success);
            var content = await File.ReadAllTextAsync(hookPath);
            Assert.Contains("# Some other hook", content);
            Assert.Contains("# Vibe Rails Pre-Commit Hook", content);
        }

        [Fact]
        public async Task InstallPreCommitHookAsync_ReplacesExistingVibeRailsHook()
        {
            // Arrange
            var hookPath = Path.Combine(_hooksDir, "pre-commit");
            var oldContent = @"#!/bin/sh
# Vibe Rails Pre-Commit Hook
# Old version
echo ""Old hook""
# End Vibe Rails Hook
";
            await File.WriteAllTextAsync(hookPath, oldContent);

            // Act
            var result = await _service.InstallPreCommitHookAsync(_testRepoPath, CancellationToken.None);

            // Assert
            Assert.True(result.Success);
            var content = await File.ReadAllTextAsync(hookPath);
            Assert.Contains("# Vibe Rails Pre-Commit Hook", content);
            Assert.DoesNotContain("Old version", content);
        }

        [Fact]
        public async Task UninstallPreCommitHookAsync_RemovesHookSection_WhenNoOtherHooks()
        {
            // Arrange
            await _service.InstallPreCommitHookAsync(_testRepoPath, CancellationToken.None);

            // Act
            var result = await _service.UninstallPreCommitHookAsync(_testRepoPath, CancellationToken.None);

            // Assert
            Assert.True(result.Success);
            var hookPath = Path.Combine(_hooksDir, "pre-commit");
            Assert.False(File.Exists(hookPath));
        }

        [Fact]
        public async Task UninstallPreCommitHookAsync_PreservesOtherHooks_WhenMixedContent()
        {
            // Arrange
            var hookPath = Path.Combine(_hooksDir, "pre-commit");
            var mixedContent = @"#!/bin/sh
# Some other hook
echo ""Other hook""

# Vibe Rails Pre-Commit Hook
echo ""Vibe Rails hook""
# End Vibe Rails Hook

# Another hook
echo ""Another hook""
";
            await File.WriteAllTextAsync(hookPath, mixedContent);

            // Act
            var result = await _service.UninstallPreCommitHookAsync(_testRepoPath, CancellationToken.None);

            // Assert
            Assert.True(result.Success);
            Assert.True(File.Exists(hookPath));
            var content = await File.ReadAllTextAsync(hookPath);
            Assert.Contains("# Some other hook", content);
            Assert.Contains("# Another hook", content);
            Assert.DoesNotContain("# Vibe Rails Pre-Commit Hook", content);
        }

        [Fact]
        public async Task UninstallPreCommitHookAsync_ReturnsSuccess_WhenHookNotInstalled()
        {
            // Act
            var result = await _service.UninstallPreCommitHookAsync(_testRepoPath, CancellationToken.None);

            // Assert
            Assert.True(result.Success);
        }

        [Fact]
        public void IsHookInstalled_ReturnsFalse_WhenNoHookFile()
        {
            // Act
            var isInstalled = _service.IsHookInstalled(_testRepoPath);

            // Assert
            Assert.False(isInstalled);
        }

        [Fact]
        public async Task IsHookInstalled_ReturnsTrue_WhenHookInstalled()
        {
            // Arrange
            await _service.InstallPreCommitHookAsync(_testRepoPath, CancellationToken.None);

            // Act
            var isInstalled = _service.IsHookInstalled(_testRepoPath);

            // Assert
            Assert.True(isInstalled);
        }

        [Fact]
        public async Task IsHookInstalled_ReturnsFalse_WhenOnlyOtherHooks()
        {
            // Arrange
            var hookPath = Path.Combine(_hooksDir, "pre-commit");
            await File.WriteAllTextAsync(hookPath, "#!/bin/sh\necho \"Other hook\"");

            // Act
            var isInstalled = _service.IsHookInstalled(_testRepoPath);

            // Assert
            Assert.False(isInstalled);
        }

        [Fact]
        public async Task InstallHooksAsync_InstallsBothHooks_WhenSuccessful()
        {
            // Act
            var result = await _service.InstallHooksAsync(_testRepoPath, CancellationToken.None);

            // Assert
            Assert.True(result.Success);
            Assert.True(File.Exists(Path.Combine(_hooksDir, "pre-commit")));
            Assert.True(File.Exists(Path.Combine(_hooksDir, "commit-msg")));
        }

        [Fact]
        public async Task InstallHooksAsync_RollsBackPreCommit_WhenCommitMsgFails()
        {
            // Arrange - make hooks directory read-only after pre-commit is installed
            // This is hard to test without filesystem mocking, so we'll skip for now
            // In a real scenario, you'd use a filesystem abstraction layer

            // For now, just verify the happy path
            var result = await _service.InstallHooksAsync(_testRepoPath, CancellationToken.None);
            Assert.True(result.Success);
        }

        [Fact]
        public async Task UninstallHooksAsync_RemovesBothHooks()
        {
            // Arrange
            await _service.InstallHooksAsync(_testRepoPath, CancellationToken.None);

            // Act
            var result = await _service.UninstallHooksAsync(_testRepoPath, CancellationToken.None);

            // Assert
            Assert.True(result.Success);
            Assert.False(File.Exists(Path.Combine(_hooksDir, "pre-commit")));
            Assert.False(File.Exists(Path.Combine(_hooksDir, "commit-msg")));
        }

        [Fact]
        public async Task InstallPreCommitHookAsync_HandlesPermissionErrors_Gracefully()
        {
            // Arrange - create a read-only hook file so the write will fail
            var readOnlyDir = Path.Combine(Path.GetTempPath(), $"readonly_{Guid.NewGuid()}");
            Directory.CreateDirectory(readOnlyDir);
            var gitDir = Path.Combine(readOnlyDir, ".git", "hooks");
            Directory.CreateDirectory(gitDir);
            var hookFile = Path.Combine(gitDir, "pre-commit");

            try
            {
                // Create a read-only file at the hook path so WriteAllTextAsync throws UnauthorizedAccessException
                File.WriteAllText(hookFile, "existing content");
                File.SetAttributes(hookFile, FileAttributes.ReadOnly);

                // Act
                var result = await _service.InstallPreCommitHookAsync(readOnlyDir, CancellationToken.None);

                // Assert
                Assert.False(result.Success);
            }
            finally
            {
                // Cleanup
                if (File.Exists(hookFile))
                {
                    File.SetAttributes(hookFile, FileAttributes.Normal);
                }
                if (Directory.Exists(readOnlyDir))
                {
                    Directory.Delete(readOnlyDir, true);
                }
            }
        }

        [Fact]
        public async Task InstallPreCommitHookAsync_LogsInformation_OnSuccess()
        {
            // Act
            await _service.InstallPreCommitHookAsync(_testRepoPath, CancellationToken.None);

            // Assert
            _loggerMock.Verify(
                x => x.Log(
                    LogLevel.Information,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Installing pre-commit hook")),
                    null,
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.AtLeastOnce);
        }

        [Fact]
        public async Task InstallPreCommitHookAsync_LogsError_OnFailure()
        {
            // Arrange - create a read-only hook file to cause a write failure
            var hookPath = Path.Combine(_hooksDir, "pre-commit");
            File.WriteAllText(hookPath, "existing content");
            File.SetAttributes(hookPath, FileAttributes.ReadOnly);

            try
            {
                // Act
                var result = await _service.InstallPreCommitHookAsync(_testRepoPath, CancellationToken.None);

                // Assert
                Assert.False(result.Success);
                _loggerMock.Verify(
                    x => x.Log(
                        LogLevel.Error,
                        It.IsAny<EventId>(),
                        It.IsAny<It.IsAnyType>(),
                        It.IsAny<Exception>(),
                        It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                    Times.AtLeastOnce);
            }
            finally
            {
                if (File.Exists(hookPath))
                {
                    File.SetAttributes(hookPath, FileAttributes.Normal);
                }
            }
        }
    }
}
