using Microsoft.Extensions.Logging;

namespace VibeRails.Services
{
    public interface IHookInstallationService
    {
        Task<HookInstallationResult> InstallPreCommitHookAsync(string repoPath, CancellationToken cancellationToken);
        Task<HookInstallationResult> UninstallPreCommitHookAsync(string repoPath, CancellationToken cancellationToken);
        Task<HookInstallationResult> InstallHooksAsync(string repoPath, CancellationToken cancellationToken);
        Task<HookInstallationResult> UninstallHooksAsync(string repoPath, CancellationToken cancellationToken);
        bool IsHookInstalled(string repoPath);
    }

    public class HookInstallationService : IHookInstallationService
    {
        private const string PRE_COMMIT_MARKER = "# Vibe Rails Pre-Commit Hook";
        private const string COMMIT_MSG_MARKER = "# Vibe Rails Commit-Msg Hook";
        private const string END_MARKER = "# End Vibe Rails Hook";
        private const string HOOK_MARKER = "# Vibe Rails Pre-Commit Hook"; // Legacy compatibility

        private readonly ILogger<HookInstallationService> _logger;

        public HookInstallationService(ILogger<HookInstallationService> logger)
        {
            _logger = logger;
        }

        public async Task<HookInstallationResult> InstallPreCommitHookAsync(string repoPath, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Installing pre-commit hook for repository: {RepoPath}", repoPath);
            return await InstallHookAsync(repoPath, "pre-commit", PRE_COMMIT_MARKER, "pre-commit-hook.sh", cancellationToken);
        }

        public async Task<HookInstallationResult> UninstallPreCommitHookAsync(string repoPath, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Uninstalling pre-commit hook for repository: {RepoPath}", repoPath);
            return await UninstallHookAsync(repoPath, "pre-commit", HOOK_MARKER, cancellationToken);
        }

        public bool IsHookInstalled(string repoPath)
        {
            var hookPath = Path.Combine(repoPath, ".git", "hooks", "pre-commit");
            if (!File.Exists(hookPath)) return false;

            var content = File.ReadAllText(hookPath);
            return content.Contains(HOOK_MARKER);
        }

        public async Task<HookInstallationResult> InstallHooksAsync(string repoPath, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Installing all hooks for repository: {RepoPath}", repoPath);

            var preCommitResult = await InstallPreCommitHookAsync(repoPath, cancellationToken);
            if (!preCommitResult.Success)
            {
                _logger.LogError("Pre-commit hook installation failed: {Error}", preCommitResult.ErrorMessage);
                return preCommitResult;
            }

            var commitMsgResult = await InstallCommitMsgHookAsync(repoPath, cancellationToken);
            if (!commitMsgResult.Success)
            {
                _logger.LogError("Commit-msg hook installation failed: {Error}", commitMsgResult.ErrorMessage);

                // Rollback pre-commit hook
                _logger.LogInformation("Rolling back pre-commit hook due to commit-msg installation failure");
                await UninstallPreCommitHookAsync(repoPath, cancellationToken);

                return HookInstallationResult.Fail(
                    HookInstallationError.PartialInstallationFailure,
                    "Commit-msg hook installation failed, rolled back pre-commit hook",
                    commitMsgResult.ErrorMessage
                );
            }

            _logger.LogInformation("All hooks installed successfully");
            return HookInstallationResult.Ok();
        }

        public async Task<HookInstallationResult> UninstallHooksAsync(string repoPath, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Uninstalling all hooks for repository: {RepoPath}", repoPath);

            var preCommitResult = await UninstallPreCommitHookAsync(repoPath, cancellationToken);
            var commitMsgResult = await UninstallCommitMsgHookAsync(repoPath, cancellationToken);

            if (!preCommitResult.Success || !commitMsgResult.Success)
            {
                _logger.LogWarning("Some hooks failed to uninstall");
                return HookInstallationResult.Fail(
                    HookInstallationError.PartialInstallationFailure,
                    "One or more hooks failed to uninstall",
                    $"Pre-commit: {preCommitResult.Success}, Commit-msg: {commitMsgResult.Success}"
                );
            }

            _logger.LogInformation("All hooks uninstalled successfully");
            return HookInstallationResult.Ok();
        }

        private async Task<HookInstallationResult> InstallCommitMsgHookAsync(string repoPath, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Installing commit-msg hook for repository: {RepoPath}", repoPath);
            return await InstallHookAsync(repoPath, "commit-msg", COMMIT_MSG_MARKER, "commit-msg-hook.sh", cancellationToken);
        }

        private async Task<HookInstallationResult> UninstallCommitMsgHookAsync(string repoPath, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Uninstalling commit-msg hook for repository: {RepoPath}", repoPath);
            return await UninstallHookAsync(repoPath, "commit-msg", COMMIT_MSG_MARKER, cancellationToken);
        }

        private async Task<HookInstallationResult> InstallHookAsync(
            string repoPath,
            string hookName,
            string marker,
            string scriptFileName,
            CancellationToken cancellationToken)
        {
            try
            {
                var hooksDir = Path.Combine(repoPath, ".git", "hooks");

                // Create hooks directory if it doesn't exist
                if (!Directory.Exists(hooksDir))
                {
                    _logger.LogInformation("Hooks directory does not exist, creating: {HooksDir}", hooksDir);
                    try
                    {
                        Directory.CreateDirectory(hooksDir);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to create hooks directory: {HooksDir}", hooksDir);
                        return HookInstallationResult.Fail(
                            HookInstallationError.HooksDirectoryCreationFailed,
                            "Failed to create .git/hooks directory",
                            ex.Message
                        );
                    }
                }

                var hookPath = Path.Combine(hooksDir, hookName);
                var hookContent = await LoadHookScriptAsync(scriptFileName);

                if (hookContent == null)
                {
                    _logger.LogError("Failed to load hook script: {ScriptFileName}", scriptFileName);
                    return HookInstallationResult.Fail(
                        HookInstallationError.ScriptResourceNotFound,
                        $"Hook script '{scriptFileName}' not found",
                        "Ensure the script exists in VibeRails/scripts/ directory"
                    );
                }

                // Handle existing hook files
                if (File.Exists(hookPath))
                {
                    _logger.LogDebug("Existing hook file found at: {HookPath}", hookPath);
                    var existing = await File.ReadAllTextAsync(hookPath, cancellationToken);

                    // Remove old Vibe Rails hook if present
                    if (existing.Contains(marker))
                    {
                        _logger.LogDebug("Removing existing Vibe Rails hook section");
                        await RemoveHookSection(hookPath, existing, marker, cancellationToken);
                        existing = File.Exists(hookPath)
                            ? await File.ReadAllTextAsync(hookPath, cancellationToken)
                            : "";
                    }

                    // Append to existing hooks
                    if (!string.IsNullOrWhiteSpace(existing))
                    {
                        _logger.LogDebug("Appending to existing hook content");
                        hookContent = existing.TrimEnd() + "\n\n" + hookContent;
                    }
                }

                // Write hook file
                await File.WriteAllTextAsync(hookPath, hookContent, cancellationToken);
                _logger.LogDebug("Hook file written: {HookPath}", hookPath);

                // Make executable on Unix systems
                if (!OperatingSystem.IsWindows())
                {
                    var chmodResult = await MakeExecutableAsync(hookPath, cancellationToken);
                    if (!chmodResult)
                    {
                        _logger.LogError("Failed to make hook executable: {HookPath}", hookPath);
                        return HookInstallationResult.Fail(
                            HookInstallationError.ChmodExecutionFailed,
                            "Failed to make hook executable (chmod failed)",
                            $"Hook file: {hookPath}"
                        );
                    }
                }

                // Verify hook was installed correctly
                if (!File.Exists(hookPath))
                {
                    _logger.LogError("Hook file verification failed: {HookPath}", hookPath);
                    return HookInstallationResult.Fail(
                        HookInstallationError.FileWriteError,
                        "Hook file not found after installation",
                        hookPath
                    );
                }

                _logger.LogInformation("Successfully installed {HookName} hook", hookName);
                return HookInstallationResult.Ok();
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogError(ex, "Permission denied while installing hook: {HookName}", hookName);
                return HookInstallationResult.Fail(
                    HookInstallationError.PermissionDenied,
                    "Permission denied accessing .git/hooks directory",
                    ex.Message
                );
            }
            catch (IOException ex)
            {
                _logger.LogError(ex, "I/O error while installing hook: {HookName}", hookName);
                return HookInstallationResult.Fail(
                    HookInstallationError.FileWriteError,
                    "Failed to write hook file",
                    ex.Message
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error installing hook: {HookName}", hookName);
                return HookInstallationResult.Fail(
                    HookInstallationError.UnknownError,
                    "Unexpected error during hook installation",
                    ex.Message
                );
            }
        }

        private async Task<HookInstallationResult> UninstallHookAsync(
            string repoPath,
            string hookName,
            string marker,
            CancellationToken cancellationToken)
        {
            try
            {
                var hookPath = Path.Combine(repoPath, ".git", "hooks", hookName);

                if (!File.Exists(hookPath))
                {
                    _logger.LogDebug("Hook file does not exist: {HookPath}", hookPath);
                    return HookInstallationResult.Ok();
                }

                var content = await File.ReadAllTextAsync(hookPath, cancellationToken);
                if (!content.Contains(marker))
                {
                    _logger.LogDebug("Hook file does not contain Vibe Rails marker: {HookPath}", hookPath);
                    return HookInstallationResult.Ok();
                }

                await RemoveHookSection(hookPath, content, marker, cancellationToken);
                _logger.LogInformation("Successfully uninstalled {HookName} hook", hookName);
                return HookInstallationResult.Ok();
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogError(ex, "Permission denied while uninstalling hook: {HookName}", hookName);
                return HookInstallationResult.Fail(
                    HookInstallationError.PermissionDenied,
                    "Permission denied accessing hook file",
                    ex.Message
                );
            }
            catch (IOException ex)
            {
                _logger.LogError(ex, "I/O error while uninstalling hook: {HookName}", hookName);
                return HookInstallationResult.Fail(
                    HookInstallationError.FileReadError,
                    "Failed to read/modify hook file",
                    ex.Message
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error uninstalling hook: {HookName}", hookName);
                return HookInstallationResult.Fail(
                    HookInstallationError.UnknownError,
                    "Unexpected error during hook uninstallation",
                    ex.Message
                );
            }
        }

        private async Task RemoveHookSection(string hookPath, string content, string marker, CancellationToken cancellationToken)
        {
            var startIndex = content.IndexOf(marker, StringComparison.Ordinal);
            var endIndex = content.IndexOf(END_MARKER, startIndex >= 0 ? startIndex : 0, StringComparison.Ordinal);

            if (startIndex >= 0 && endIndex >= 0)
            {
                var before = content.Substring(0, startIndex);
                var after = content.Substring(endIndex + END_MARKER.Length);
                var newContent = (before + after).Trim();

                if (string.IsNullOrWhiteSpace(newContent) || newContent == "#!/bin/sh")
                {
                    _logger.LogDebug("Deleting hook file as it only contained Vibe Rails content: {HookPath}", hookPath);
                    File.Delete(hookPath);
                }
                else
                {
                    _logger.LogDebug("Preserving hook file with other content: {HookPath}", hookPath);
                    await File.WriteAllTextAsync(hookPath, newContent, cancellationToken);
                }
            }
            else if (startIndex >= 0)
            {
                // Handle case where end marker is missing (shouldn't happen, but be defensive)
                _logger.LogWarning("End marker not found for hook section, removing from start marker to end of file");
                var newContent = content.Substring(0, startIndex).Trim();
                if (string.IsNullOrWhiteSpace(newContent) || newContent == "#!/bin/sh")
                {
                    File.Delete(hookPath);
                }
                else
                {
                    await File.WriteAllTextAsync(hookPath, newContent, cancellationToken);
                }
            }
        }

        private async Task<string?> LoadHookScriptAsync(string scriptFileName)
        {
            try
            {
                // Use AppContext.BaseDirectory for AOT compatibility
                var assemblyDir = AppContext.BaseDirectory;
                if (string.IsNullOrEmpty(assemblyDir))
                {
                    _logger.LogError("Could not determine application directory");
                    return null;
                }

                var scriptPath = Path.Combine(assemblyDir, "scripts", scriptFileName);

                if (!File.Exists(scriptPath))
                {
                    _logger.LogError("Hook script not found at: {ScriptPath}", scriptPath);
                    return null;
                }

                var content = await File.ReadAllTextAsync(scriptPath);
                _logger.LogDebug("Loaded hook script: {ScriptFileName}", scriptFileName);
                return content;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load hook script: {ScriptFileName}", scriptFileName);
                return null;
            }
        }

        private async Task<bool> MakeExecutableAsync(string path, CancellationToken cancellationToken)
        {
            try
            {
                var process = new System.Diagnostics.Process
                {
                    StartInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "chmod",
                        Arguments = $"+x \"{path}\"",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };

                process.Start();
                await process.WaitForExitAsync(cancellationToken);

                if (process.ExitCode != 0)
                {
                    var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
                    _logger.LogError("chmod failed with exit code {ExitCode}: {Error}", process.ExitCode, stderr);
                    return false;
                }

                _logger.LogDebug("Successfully made file executable: {Path}", path);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception while executing chmod: {Path}", path);
                return false;
            }
        }
    }
}
