using System.Diagnostics;
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
        private readonly string _scriptsDir;
        private readonly Mock<ILogger<HookInstallationService>> _loggerMock;
        private readonly HookInstallationService _service;

        public HookInstallationServiceTests()
        {
            // Create a temporary directory for testing
            _testRepoPath = Path.Combine(Path.GetTempPath(), $"vb_test_{Guid.NewGuid()}");
            _hooksDir = Path.Combine(_testRepoPath, ".git", "hooks");
            _scriptsDir = Path.Combine(_testRepoPath, "hook-templates");
            Directory.CreateDirectory(_hooksDir);
            Directory.CreateDirectory(_scriptsDir);

            _loggerMock = new Mock<ILogger<HookInstallationService>>();
            CreateMockScripts();
            _service = new HookInstallationService(_loggerMock.Object, _scriptsDir);
        }

        private void CreateMockScripts()
        {
            var preCommitScript = @"#!/bin/sh
# Vibe Rails Pre-Commit Hook
# VibeRails Hook Version: 3
VIBERAILS_EXECUTABLE='__VIBERAILS_EXECUTABLE__'
VIBERAILS_EXECUTABLE_ARGUMENT='__VIBERAILS_EXECUTABLE_ARGUMENT__'
VIBERAILS_CHAINED_HOOK='__VIBERAILS_CHAINED_HOOK__'
vb --vca-hook pre-commit
if [ -n ""$VIBERAILS_CHAINED_HOOK"" ]; then ""$VIBERAILS_CHAINED_HOOK"" ""$@""; fi
# End Vibe Rails Hook
";
            File.WriteAllText(Path.Combine(_scriptsDir, "pre-commit-hook.sh"), preCommitScript);

            var commitMsgScript = @"#!/bin/sh
# Vibe Rails Commit-Msg Hook
# VibeRails Hook Version: 3
VIBERAILS_EXECUTABLE='__VIBERAILS_EXECUTABLE__'
VIBERAILS_EXECUTABLE_ARGUMENT='__VIBERAILS_EXECUTABLE_ARGUMENT__'
VIBERAILS_CHAINED_HOOK='__VIBERAILS_CHAINED_HOOK__'
if [ -n ""$VIBERAILS_CHAINED_HOOK"" ]; then ""$VIBERAILS_CHAINED_HOOK"" ""$@""; fi
vb --vca-hook commit-msg --commit-message ""$1""
# End Vibe Rails Hook
";
            File.WriteAllText(Path.Combine(_scriptsDir, "commit-msg-hook.sh"), commitMsgScript);
        }

        [Fact]
        public void CreateChmodStartInfo_PreservesQuotedPathAsSingleArgument()
        {
            const string path = "/tmp/repo's hooks/pre-commit";

            var startInfo = HookInstallationService.CreateChmodStartInfo(path);

            Assert.Equal("chmod", startInfo.FileName);
            Assert.Empty(startInfo.Arguments);
            Assert.Equal(new[] { "+x", path }, startInfo.ArgumentList);
            Assert.False(startInfo.UseShellExecute);
        }

        public void Dispose()
        {
            // Clean up test directory
            if (Directory.Exists(_testRepoPath))
            {
                foreach (var path in Directory.EnumerateFiles(_testRepoPath, "*", SearchOption.AllDirectories))
                {
                    if (new FileInfo(path).LinkTarget == null)
                    {
                        File.SetAttributes(path, FileAttributes.Normal);
                    }
                }
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

            var content = await File.ReadAllTextAsync(hookPath, TestContext.Current.CancellationToken);
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
        public async Task InstallPreCommitHookAsync_RunsBeforeExistingHook_WhenOtherHookExits()
        {
            // Arrange
            var hookPath = Path.Combine(_hooksDir, "pre-commit");
            var existingContent = @"#!/bin/sh
# Some other hook
echo ""Other hook""
exit 0
";
            await File.WriteAllTextAsync(hookPath, existingContent, TestContext.Current.CancellationToken);

            // Act
            var result = await _service.InstallPreCommitHookAsync(_testRepoPath, CancellationToken.None);

            // Assert
            Assert.True(result.Success);
            var content = await File.ReadAllTextAsync(hookPath, TestContext.Current.CancellationToken);
            Assert.Contains("# Some other hook", content);
            Assert.Contains("# Vibe Rails Pre-Commit Hook", content);
            Assert.True(
                content.IndexOf("# Vibe Rails Pre-Commit Hook", StringComparison.Ordinal)
                < content.IndexOf("# Some other hook", StringComparison.Ordinal));
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
            await File.WriteAllTextAsync(hookPath, oldContent, TestContext.Current.CancellationToken);

            // Act
            var result = await _service.InstallPreCommitHookAsync(_testRepoPath, CancellationToken.None);

            // Assert
            Assert.True(result.Success);
            var content = await File.ReadAllTextAsync(hookPath, TestContext.Current.CancellationToken);
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
            await File.WriteAllTextAsync(hookPath, mixedContent, TestContext.Current.CancellationToken);

            // Act
            var result = await _service.UninstallPreCommitHookAsync(_testRepoPath, CancellationToken.None);

            // Assert
            Assert.True(result.Success);
            Assert.True(File.Exists(hookPath));
            var content = await File.ReadAllTextAsync(hookPath, TestContext.Current.CancellationToken);
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
        public async Task IsHookInstalled_ReturnsTrue_WhenHooksInstalled()
        {
            // Arrange
            await _service.InstallHooksAsync(_testRepoPath, CancellationToken.None);

            // Act
            var isInstalled = _service.IsHookInstalled(_testRepoPath);

            // Assert
            Assert.True(isInstalled);
        }

        [Fact]
        public async Task IsHookInstalled_ReturnsFalse_WhenOnlyPreCommitHookInstalled()
        {
            // Arrange
            await _service.InstallPreCommitHookAsync(_testRepoPath, CancellationToken.None);

            // Act
            var isInstalled = _service.IsHookInstalled(_testRepoPath);

            // Assert
            Assert.False(isInstalled);
        }

        [Fact]
        public async Task IsHookInstalled_ReturnsFalse_WhenOnlyOtherHooks()
        {
            // Arrange
            var hookPath = Path.Combine(_hooksDir, "pre-commit");
            await File.WriteAllTextAsync(
                hookPath,
                "#!/bin/sh\necho \"Other hook\"",
                TestContext.Current.CancellationToken);

            // Act
            var isInstalled = _service.IsHookInstalled(_testRepoPath);

            // Assert
            Assert.False(isInstalled);
        }

        [Fact]
        public async Task GetStatusAsync_ReportsDisabledMarkedHooksAsNeedingRepair()
        {
            await File.WriteAllTextAsync(
                Path.Combine(_hooksDir, "pre-commit"),
                "#!/bin/sh\n# Vibe Rails Pre-Commit Hook\necho temporarily disabled\n# End Vibe Rails Hook",
                TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(_hooksDir, "commit-msg"),
                "#!/bin/sh\n# Vibe Rails Commit-Msg Hook\necho temporarily disabled\n# End Vibe Rails Hook",
                TestContext.Current.CancellationToken);

            var status = await _service.GetStatusAsync(
                _testRepoPath,
                TestContext.Current.CancellationToken);

            Assert.False(status.IsInstalled);
            Assert.True(status.NeedsRepair);
            Assert.Equal(GitHookFileState.Disabled, status.PreCommit.State);
            Assert.Equal(GitHookFileState.Disabled, status.CommitMessage.State);
        }

        [Theory]
        [InlineData("dotnet")]
        [InlineData("dotnet.exe")]
        public void ResolveLaunchCommand_IncludesEntryDll_ForDotNetHostOnEveryPlatform(
            string dotnetFileName)
        {
            var hostPath = Path.Combine(_testRepoPath, dotnetFileName);
            var entryDll = Path.Combine(_testRepoPath, "vb.dll");

            var command = HookInstallationService.ResolveLaunchCommand(
                hostPath,
                [entryDll, "--some-option"]);

            Assert.Equal(Path.GetFullPath(hostPath), command.Executable);
            Assert.Equal(Path.GetFullPath(entryDll), command.Argument);
        }

        [Fact]
        public async Task GetStatusAsync_RequiresCurrentExecutableAndEntryDllToExistAndMatch()
        {
            var hostPath = Path.Combine(_testRepoPath, "dotnet.exe");
            var installedDll = Path.Combine(_testRepoPath, "vb-installed.dll");
            var otherDll = Path.Combine(_testRepoPath, "vb-other.dll");
            await File.WriteAllTextAsync(hostPath, "host", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(installedDll, "installed", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(otherDll, "other", TestContext.Current.CancellationToken);

            var installingService = new HookInstallationService(
                _loggerMock.Object,
                _scriptsDir,
                new HookInstallationService.HookLaunchCommand(hostPath, installedDll));
            Assert.True((await installingService.InstallHooksAsync(
                _testRepoPath,
                TestContext.Current.CancellationToken)).Success);

            var differentLaunchService = new HookInstallationService(
                _loggerMock.Object,
                _scriptsDir,
                new HookInstallationService.HookLaunchCommand(hostPath, otherDll));
            var mismatched = await differentLaunchService.GetStatusAsync(
                _testRepoPath,
                TestContext.Current.CancellationToken);
            Assert.Equal(GitHookFileState.Stale, mismatched.PreCommit.State);
            Assert.Equal(GitHookFileState.Stale, mismatched.CommitMessage.State);

            File.Delete(installedDll);
            var missingArgument = await installingService.GetStatusAsync(
                _testRepoPath,
                TestContext.Current.CancellationToken);
            Assert.Contains("argument does not exist", missingArgument.PreCommit.Message);

            File.Delete(hostPath);
            var missingExecutable = await installingService.GetStatusAsync(
                _testRepoPath,
                TestContext.Current.CancellationToken);
            Assert.Contains("executable does not exist", missingExecutable.PreCommit.Message);
        }

        [Fact]
        public async Task InstallHooksAsync_HonorsConfiguredCoreHooksPath()
        {
            await RunGitAsync("init");
            await RunGitAsync("config", "core.hooksPath", ".custom-hooks");

            var result = await _service.InstallHooksAsync(
                _testRepoPath,
                TestContext.Current.CancellationToken);
            var status = await _service.GetStatusAsync(
                _testRepoPath,
                TestContext.Current.CancellationToken);

            Assert.True(result.Success);
            Assert.True(status.IsInstalled);
            Assert.EndsWith(".custom-hooks", status.HooksPath, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(Path.Combine(_testRepoPath, ".custom-hooks", "pre-commit")));
            Assert.True(File.Exists(Path.Combine(_testRepoPath, ".custom-hooks", "commit-msg")));
        }

        [Fact]
        public async Task InstallAndUninstall_PreservesNonShellHookThroughSidecar()
        {
            const string pythonHook = "#!/usr/bin/env python3\nprint('third party hook')\n";
            var hookPath = Path.Combine(_hooksDir, "pre-commit");
            await File.WriteAllTextAsync(
                hookPath,
                pythonHook,
                TestContext.Current.CancellationToken);

            var installResult = await _service.InstallPreCommitHookAsync(
                _testRepoPath,
                TestContext.Current.CancellationToken);

            Assert.True(installResult.Success);
            Assert.Equal(
                pythonHook,
                await File.ReadAllTextAsync(
                    hookPath + ".viberails-chain",
                    TestContext.Current.CancellationToken));
            Assert.Contains(
                ".viberails-chain",
                await File.ReadAllTextAsync(hookPath, TestContext.Current.CancellationToken));

            var uninstallResult = await _service.UninstallPreCommitHookAsync(
                _testRepoPath,
                TestContext.Current.CancellationToken);

            Assert.True(uninstallResult.Success);
            Assert.Equal(
                pythonHook,
                await File.ReadAllTextAsync(hookPath, TestContext.Current.CancellationToken));
            Assert.False(File.Exists(hookPath + ".viberails-chain"));
        }

        [Fact]
        public async Task InstallHooksAsync_PreservesExistingCommitMsgHookAsSidecarBeforeVca()
        {
            const string existingCommitMsg = "#!/bin/sh\necho third-party >> \"$1\"\n";
            var commitMsgPath = Path.Combine(_hooksDir, "commit-msg");
            await File.WriteAllTextAsync(
                commitMsgPath,
                existingCommitMsg,
                TestContext.Current.CancellationToken);

            var result = await _service.InstallHooksAsync(
                _testRepoPath,
                TestContext.Current.CancellationToken);

            Assert.True(result.Success);
            Assert.Equal(
                existingCommitMsg,
                await File.ReadAllTextAsync(
                    commitMsgPath + ".viberails-chain",
                    TestContext.Current.CancellationToken));

            var installed = await File.ReadAllTextAsync(
                commitMsgPath,
                TestContext.Current.CancellationToken);
            Assert.DoesNotContain("third-party", installed);
            var chainedIndex = installed.IndexOf("\"$VIBERAILS_CHAINED_HOOK\" \"$@\"", StringComparison.Ordinal);
            var vcaIndex = installed.IndexOf("vb --vca-hook commit-msg", StringComparison.Ordinal);
            Assert.True(chainedIndex >= 0, installed);
            Assert.True(vcaIndex >= 0, installed);
            Assert.True(chainedIndex < vcaIndex, installed);
        }

        [Fact]
        public async Task InstallAndUninstall_PreservesBinaryHookBytesThroughSidecar()
        {
            var binaryHook = new byte[] { 0x7f, 0x45, 0x4c, 0x46, 0x00, 0xff, 0x10, 0x80 };
            var hookPath = Path.Combine(_hooksDir, "pre-commit");
            await File.WriteAllBytesAsync(
                hookPath,
                binaryHook,
                TestContext.Current.CancellationToken);

            var installResult = await _service.InstallPreCommitHookAsync(
                _testRepoPath,
                TestContext.Current.CancellationToken);

            Assert.True(installResult.Success);
            Assert.Equal(
                binaryHook,
                await File.ReadAllBytesAsync(
                    hookPath + ".viberails-chain",
                    TestContext.Current.CancellationToken));

            var uninstallResult = await _service.UninstallPreCommitHookAsync(
                _testRepoPath,
                TestContext.Current.CancellationToken);

            Assert.True(uninstallResult.Success);
            Assert.Equal(
                binaryHook,
                await File.ReadAllBytesAsync(hookPath, TestContext.Current.CancellationToken));
        }

        [Fact]
        public async Task InstallAndUninstall_PreservesSymbolicLinkIdentityThroughSidecar()
        {
            var targetPath = Path.Combine(_testRepoPath, "third-party-pre-commit.py");
            var hookPath = Path.Combine(_hooksDir, "pre-commit");
            await File.WriteAllTextAsync(
                targetPath,
                "#!/usr/bin/env python3\nprint('third party')\n",
                TestContext.Current.CancellationToken);

            try
            {
                File.CreateSymbolicLink(hookPath, targetPath);
            }
            catch (Exception ex) when (
                ex is UnauthorizedAccessException or PlatformNotSupportedException ||
                (OperatingSystem.IsWindows() && ex is IOException))
            {
                // Some Windows hosts do not grant symlink creation to test processes.
                return;
            }

            var originalTarget = new FileInfo(hookPath).LinkTarget;
            var installResult = await _service.InstallPreCommitHookAsync(
                _testRepoPath,
                TestContext.Current.CancellationToken);

            Assert.True(installResult.Success);
            Assert.Null(new FileInfo(hookPath).LinkTarget);
            Assert.Equal(originalTarget, new FileInfo(hookPath + ".viberails-chain").LinkTarget);

            var uninstallResult = await _service.UninstallPreCommitHookAsync(
                _testRepoPath,
                TestContext.Current.CancellationToken);

            Assert.True(uninstallResult.Success);
            Assert.Equal(originalTarget, new FileInfo(hookPath).LinkTarget);
            Assert.Equal(
                "#!/usr/bin/env python3\nprint('third party')\n",
                await File.ReadAllTextAsync(targetPath, TestContext.Current.CancellationToken));
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
        public async Task InstallHooksAsync_RollsBackPreCommit_WhenCommitMsgInstallFails()
        {
            // Arrange: pre-create a commit-msg directory so its write fails while the
            // pre-commit write succeeds. A partial install must roll back so nothing is left
            // reporting as installed while acknowledgment enforcement is actually missing.
            var commitMsgPath = Path.Combine(_hooksDir, "commit-msg");
            Directory.CreateDirectory(commitMsgPath);

            // Act
            var result = await _service.InstallHooksAsync(_testRepoPath, CancellationToken.None);

            // Assert
            Assert.False(result.Success);
            Assert.False(File.Exists(Path.Combine(_hooksDir, "pre-commit")));
            Assert.False(_service.IsHookInstalled(_testRepoPath));
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
                File.WriteAllText(hookFile, "#!/bin/sh\necho existing\n");
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
            File.WriteAllText(hookPath, "#!/bin/sh\necho existing\n");
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

        [Fact]
        public async Task InstallHooksAsync_CancellationRestoresBothHooksWithNonCancelledRollback()
        {
            var preCommitPath = Path.Combine(_hooksDir, "pre-commit");
            var commitMsgPath = Path.Combine(_hooksDir, "commit-msg");
            var originalPreCommit = "#!/bin/sh\necho original-pre-commit\n";
            var originalCommitMsg = "#!/bin/sh\necho original-commit-msg\n";
            await File.WriteAllTextAsync(
                preCommitPath,
                originalPreCommit,
                TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(
                commitMsgPath,
                originalCommitMsg,
                TestContext.Current.CancellationToken);

            var fakeExecutable = Path.Combine(_testRepoPath, "vb-test-host");
            await File.WriteAllTextAsync(
                fakeExecutable,
                "test host",
                TestContext.Current.CancellationToken);
            using var cancellation = new CancellationTokenSource();
            var service = new HookInstallationService(
                _loggerMock.Object,
                _scriptsDir,
                new HookInstallationService.HookLaunchCommand(fakeExecutable, string.Empty),
                (hookName, token) =>
                {
                    if (hookName == "commit-msg")
                    {
                        cancellation.Cancel();
                        token.ThrowIfCancellationRequested();
                    }

                    return Task.CompletedTask;
                });

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => service.InstallHooksAsync(_testRepoPath, cancellation.Token));

            Assert.Equal(
                originalPreCommit,
                await File.ReadAllTextAsync(preCommitPath, TestContext.Current.CancellationToken));
            Assert.Equal(
                originalCommitMsg,
                await File.ReadAllTextAsync(commitMsgPath, TestContext.Current.CancellationToken));
            Assert.False(File.Exists(preCommitPath + ".viberails-chain"));
            Assert.False(File.Exists(commitMsgPath + ".viberails-chain"));
        }

        [Fact]
        public async Task InstallHooksAsync_ReportsRollbackFailureInsteadOfClaimingRestore()
        {
            var preCommitPath = Path.Combine(_hooksDir, "pre-commit");
            await File.WriteAllTextAsync(
                preCommitPath,
                "#!/bin/sh\necho original\n",
                TestContext.Current.CancellationToken);
            var fakeExecutable = Path.Combine(_testRepoPath, "vb-test-host");
            await File.WriteAllTextAsync(
                fakeExecutable,
                "test host",
                TestContext.Current.CancellationToken);
            var service = new HookInstallationService(
                _loggerMock.Object,
                _scriptsDir,
                new HookInstallationService.HookLaunchCommand(fakeExecutable, string.Empty),
                (hookName, _) =>
                {
                    if (hookName == "commit-msg")
                    {
                        foreach (var path in Directory.EnumerateFiles(_hooksDir))
                        {
                            File.SetAttributes(path, FileAttributes.Normal);
                            File.Delete(path);
                        }

                        Directory.Delete(_hooksDir);
                        File.WriteAllText(_hooksDir, "blocks hook restoration");
                    }

                    return Task.CompletedTask;
                });

            var result = await service.InstallHooksAsync(
                _testRepoPath,
                TestContext.Current.CancellationToken);

            Assert.False(result.Success);
            Assert.Equal(HookInstallationError.PartialInstallationFailure, result.ErrorType);
            Assert.Contains("rollback was incomplete", result.ErrorMessage);
            Assert.Contains("Rollback failures", result.Details);
            Assert.Contains(preCommitPath, result.Details);
        }

        [Fact]
        public async Task InstallHooksAsync_KeepsPreservedSidecarWhenPrimaryRollbackFails()
        {
            const string originalPreCommit = "#!/usr/bin/env python3\nprint('original')\n";
            var preCommitPath = Path.Combine(_hooksDir, "pre-commit");
            var commitMsgPath = Path.Combine(_hooksDir, "commit-msg");
            await File.WriteAllTextAsync(
                preCommitPath,
                originalPreCommit,
                TestContext.Current.CancellationToken);

            var fakeExecutable = Path.Combine(_testRepoPath, "vb-test-host");
            await File.WriteAllTextAsync(
                fakeExecutable,
                "test host",
                TestContext.Current.CancellationToken);
            var service = new HookInstallationService(
                _loggerMock.Object,
                _scriptsDir,
                new HookInstallationService.HookLaunchCommand(fakeExecutable, string.Empty),
                (hookName, _) =>
                {
                    if (hookName == "commit-msg")
                    {
                        File.Delete(preCommitPath);
                        Directory.CreateDirectory(preCommitPath);
                        Directory.CreateDirectory(commitMsgPath);
                    }

                    return Task.CompletedTask;
                });

            var result = await service.InstallHooksAsync(
                _testRepoPath,
                TestContext.Current.CancellationToken);

            Assert.False(result.Success);
            Assert.Equal(HookInstallationError.PartialInstallationFailure, result.ErrorType);
            Assert.True(Directory.Exists(preCommitPath));
            Assert.Equal(
                originalPreCommit,
                await File.ReadAllTextAsync(
                    preCommitPath + ".viberails-chain",
                    TestContext.Current.CancellationToken));
        }

        [Fact]
        public async Task InstallHooksAsync_CancellationThrowsAggregateWhenRollbackFails()
        {
            var preCommitPath = Path.Combine(_hooksDir, "pre-commit");
            await File.WriteAllTextAsync(
                preCommitPath,
                "#!/bin/sh\necho original\n",
                TestContext.Current.CancellationToken);
            var fakeExecutable = Path.Combine(_testRepoPath, "vb-test-host");
            await File.WriteAllTextAsync(
                fakeExecutable,
                "test host",
                TestContext.Current.CancellationToken);
            using var cancellation = new CancellationTokenSource();
            var service = new HookInstallationService(
                _loggerMock.Object,
                _scriptsDir,
                new HookInstallationService.HookLaunchCommand(fakeExecutable, string.Empty),
                (hookName, token) =>
                {
                    if (hookName == "commit-msg")
                    {
                        foreach (var path in Directory.EnumerateFiles(_hooksDir))
                        {
                            File.SetAttributes(path, FileAttributes.Normal);
                            File.Delete(path);
                        }

                        Directory.Delete(_hooksDir);
                        File.WriteAllText(_hooksDir, "blocks hook restoration");
                        cancellation.Cancel();
                        token.ThrowIfCancellationRequested();
                    }

                    return Task.CompletedTask;
                });

            var exception = await Assert.ThrowsAsync<AggregateException>(
                () => service.InstallHooksAsync(_testRepoPath, cancellation.Token));

            Assert.Contains("cancelled and rollback was incomplete", exception.Message);
            Assert.Contains(exception.InnerExceptions, inner => inner is OperationCanceledException);
            Assert.Contains(exception.InnerExceptions, inner => inner is IOException);
        }

        [Fact]
        public async Task InstallHooksAsync_CancellationRestoresSymbolicLinkSnapshot()
        {
            var targetPath = Path.Combine(_testRepoPath, "original-hook.py");
            var preCommitPath = Path.Combine(_hooksDir, "pre-commit");
            await File.WriteAllTextAsync(
                targetPath,
                "#!/usr/bin/env python3\nprint('original')\n",
                TestContext.Current.CancellationToken);
            try
            {
                File.CreateSymbolicLink(preCommitPath, targetPath);
            }
            catch (Exception ex) when (
                ex is UnauthorizedAccessException or PlatformNotSupportedException ||
                (OperatingSystem.IsWindows() && ex is IOException))
            {
                return;
            }

            var originalTarget = new FileInfo(preCommitPath).LinkTarget;
            var fakeExecutable = Path.Combine(_testRepoPath, "vb-test-host");
            await File.WriteAllTextAsync(
                fakeExecutable,
                "test host",
                TestContext.Current.CancellationToken);
            using var cancellation = new CancellationTokenSource();
            var service = new HookInstallationService(
                _loggerMock.Object,
                _scriptsDir,
                new HookInstallationService.HookLaunchCommand(fakeExecutable, string.Empty),
                (hookName, token) =>
                {
                    if (hookName == "commit-msg")
                    {
                        cancellation.Cancel();
                        token.ThrowIfCancellationRequested();
                    }

                    return Task.CompletedTask;
                });

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => service.InstallHooksAsync(_testRepoPath, cancellation.Token));

            Assert.Equal(originalTarget, new FileInfo(preCommitPath).LinkTarget);
            Assert.False(File.Exists(preCommitPath + ".viberails-chain"));
            Assert.Equal(
                "#!/usr/bin/env python3\nprint('original')\n",
                await File.ReadAllTextAsync(targetPath, TestContext.Current.CancellationToken));
        }

        private async Task RunGitAsync(params string[] arguments)
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "git",
                    WorkingDirectory = _testRepoPath,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            foreach (var argument in arguments)
            {
                process.StartInfo.ArgumentList.Add(argument);
            }

            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            var error = await process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);

            Assert.True(
                process.ExitCode == 0,
                $"git {string.Join(' ', arguments)} failed.\n{output}\n{error}");
        }
    }
}
