using System.Text;
using Microsoft.Extensions.Logging;

namespace VibeRails.Services
{
    public interface IHookInstallationService
    {
        Task<HookInstallationResult> InstallPreCommitHookAsync(string repoPath, CancellationToken cancellationToken);
        Task<HookInstallationResult> UninstallPreCommitHookAsync(string repoPath, CancellationToken cancellationToken);
        Task<HookInstallationResult> InstallHooksAsync(string repoPath, CancellationToken cancellationToken);
        Task<HookInstallationResult> UninstallHooksAsync(string repoPath, CancellationToken cancellationToken);
        Task<GitHooksStatus> GetStatusAsync(string repoPath, CancellationToken cancellationToken);
        bool IsHookInstalled(string repoPath);
    }

    public class HookInstallationService : IHookInstallationService
    {
        private const string PRE_COMMIT_MARKER = "# Vibe Rails Pre-Commit Hook";
        private const string COMMIT_MSG_MARKER = "# Vibe Rails Commit-Msg Hook";
        private const string POST_COMMIT_MARKER = "# Vibe Rails Post-Commit Hook";
        private const string END_MARKER = "# End Vibe Rails Hook";
        private const string DISABLED_MARKER = "# VibeRails: disabled";
        private const string HOOK_MARKER = "# Vibe Rails Pre-Commit Hook"; // Legacy compatibility
        private const string VERSION_MARKER_PREFIX = "# VibeRails Hook Version: ";
        private const string VERSION_PLACEHOLDER = "__VIBERAILS_HOOK_VERSION__";
        private const string EXECUTABLE_PLACEHOLDER = "__VIBERAILS_EXECUTABLE__";
        private const string EXECUTABLE_ARGUMENT_PLACEHOLDER = "__VIBERAILS_EXECUTABLE_ARGUMENT__";
        private const string CHAINED_HOOK_PLACEHOLDER = "__VIBERAILS_CHAINED_HOOK__";
        private const string CHAINED_HOOK_SUFFIX = ".viberails-chain";
        private static readonly TimeSpan GitPathTimeout = TimeSpan.FromSeconds(5);

        private readonly ILogger<HookInstallationService> _logger;
        private readonly string _scriptsDirectory;
        private readonly HookLaunchCommand _launchCommand;
        private readonly string _hookVersion;
        private readonly Func<string, CancellationToken, Task>? _installationCheckpoint;

        public HookInstallationService(
            ILogger<HookInstallationService> logger,
            string? scriptsDirectory = null)
            : this(
                logger,
                scriptsDirectory,
                GetCurrentLaunchCommand(),
                installationCheckpoint: null,
                hookVersion: VersionInfo.Version)
        {
        }

        internal HookInstallationService(
            ILogger<HookInstallationService> logger,
            string? scriptsDirectory,
            HookLaunchCommand launchCommand,
            Func<string, CancellationToken, Task>? installationCheckpoint = null,
            string? hookVersion = null)
        {
            _logger = logger;
            _scriptsDirectory = scriptsDirectory ?? Path.Combine(AppContext.BaseDirectory, "scripts");
            _launchCommand = launchCommand;
            var configuredVersion = hookVersion ?? VersionInfo.Version;
            if (!TryNormalizeHookVersion(configuredVersion, out _hookVersion))
            {
                _logger.LogWarning(
                    "The configured hook version {ConfiguredVersion} is not a SemVer-style value, so "
                    + "installed hooks will be stamped {FallbackVersion} instead. Set "
                    + "the application version to letters, digits, '.', '-' or '+' only — the stamp is "
                    + "written into a shell script unescaped, so other characters are refused.",
                    configuredVersion,
                    _hookVersion);
            }

            _installationCheckpoint = installationCheckpoint;
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
            // Retained for callers that cannot await. Production status/repair paths use
            // GetStatusAsync so worktrees and core.hooksPath are resolved by Git.
            var hooksPath = Path.Combine(repoPath, ".git", "hooks");
            return InspectHookFile(Path.Combine(hooksPath, "pre-commit"), "pre-commit", PRE_COMMIT_MARKER).IsCurrent
                && InspectHookFile(Path.Combine(hooksPath, "commit-msg"), "commit-msg", COMMIT_MSG_MARKER).IsCurrent
                && InspectHookFile(Path.Combine(hooksPath, "post-commit"), "post-commit", POST_COMMIT_MARKER).IsCurrent;
        }

        public async Task<GitHooksStatus> GetStatusAsync(
            string repoPath,
            CancellationToken cancellationToken)
        {
            var hooksPath = await ResolveHooksDirectoryAsync(repoPath, cancellationToken);
            return new GitHooksStatus(
                Path.GetFullPath(repoPath),
                hooksPath,
                InspectHookFile(Path.Combine(hooksPath, "pre-commit"), "pre-commit", PRE_COMMIT_MARKER),
                InspectHookFile(Path.Combine(hooksPath, "commit-msg"), "commit-msg", COMMIT_MSG_MARKER),
                InspectHookFile(Path.Combine(hooksPath, "post-commit"), "post-commit", POST_COMMIT_MARKER));
        }

        public async Task<HookInstallationResult> InstallHooksAsync(string repoPath, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Installing all hooks for repository: {RepoPath}", repoPath);

            List<HookFileSnapshot> snapshots;
            try
            {
                var hooksPath = await ResolveHooksDirectoryAsync(repoPath, cancellationToken);
                snapshots = await CaptureSnapshotsAsync(hooksPath, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to inspect existing hooks before installation");
                return HookInstallationResult.Fail(
                    HookInstallationError.FileReadError,
                    "Could not inspect existing Git hooks before installation",
                    ex.Message);
            }

            try
            {
                var preCommitResult = await InstallPreCommitHookAsync(repoPath, cancellationToken);
                if (!preCommitResult.Success)
                {
                    _logger.LogError("Pre-commit hook installation failed: {Error}", preCommitResult.ErrorMessage);
                    var rollback = await RestoreSnapshotsAsync(snapshots, CancellationToken.None);
                    if (!rollback.Success)
                    {
                        return HookInstallationResult.Fail(
                            HookInstallationError.PartialInstallationFailure,
                            "Pre-commit hook installation failed and rollback was incomplete",
                            FormatFailureWithRollback(preCommitResult, rollback));
                    }

                    return preCommitResult;
                }

                var commitMsgResult = await InstallCommitMsgHookAsync(repoPath, cancellationToken);
                if (!commitMsgResult.Success)
                {
                    _logger.LogError("Commit-msg hook installation failed: {Error}", commitMsgResult.ErrorMessage);

                    // Restore byte-for-byte snapshots so a failed repair does not delete an older
                    // working VibeRails section or a third-party hook.
                    _logger.LogInformation("Restoring hook snapshots after commit-msg installation failure");
                    var rollback = await RestoreSnapshotsAsync(snapshots, CancellationToken.None);
                    if (!rollback.Success)
                    {
                        return HookInstallationResult.Fail(
                            HookInstallationError.PartialInstallationFailure,
                            "Commit-msg hook installation failed and rollback was incomplete",
                            FormatFailureWithRollback(commitMsgResult, rollback));
                    }

                    return HookInstallationResult.Fail(
                        HookInstallationError.PartialInstallationFailure,
                        "Commit-msg hook installation failed; previous hooks were restored",
                        commitMsgResult.ErrorMessage
                    );
                }

                var postCommitResult = await InstallPostCommitHookAsync(repoPath, cancellationToken);
                if (!postCommitResult.Success)
                {
                    _logger.LogError("Post-commit hook installation failed: {Error}", postCommitResult.ErrorMessage);
                    var rollback = await RestoreSnapshotsAsync(snapshots, CancellationToken.None);
                    if (!rollback.Success)
                    {
                        return HookInstallationResult.Fail(
                            HookInstallationError.PartialInstallationFailure,
                            "Post-commit hook installation failed and rollback was incomplete",
                            FormatFailureWithRollback(postCommitResult, rollback));
                    }

                    return HookInstallationResult.Fail(
                        HookInstallationError.PartialInstallationFailure,
                        "Post-commit hook installation failed; previous hooks were restored",
                        postCommitResult.ErrorMessage);
                }

                _logger.LogInformation("All hooks installed successfully");
                return HookInstallationResult.Ok();
            }
            catch (OperationCanceledException cancellationException) when (cancellationToken.IsCancellationRequested)
            {
                // Cancellation must not interrupt rollback. Once the first hook is touched,
                // the three-hook install is a transaction and restores from its snapshots.
                _logger.LogInformation("Hook installation was cancelled; restoring hook snapshots");
                var rollback = await RestoreSnapshotsAsync(snapshots, CancellationToken.None);
                if (!rollback.Success)
                {
                    var failures = new List<Exception> { cancellationException };
                    failures.AddRange(rollback.Failures.Select(failure =>
                        new IOException(
                            $"Failed to restore hook snapshot '{failure.Path}'.",
                            failure.Exception)));
                    throw new AggregateException(
                        "Hook installation was cancelled and rollback was incomplete.",
                        failures);
                }

                throw;
            }
        }

        private static string FormatFailureWithRollback(
            HookInstallationResult installationFailure,
            HookRollbackResult rollback)
        {
            var original = string.IsNullOrWhiteSpace(installationFailure.Details)
                ? installationFailure.ErrorMessage
                : $"{installationFailure.ErrorMessage}: {installationFailure.Details}";
            return $"Original failure: {original}. Rollback failures: {rollback.DescribeFailures()}";
        }

        public async Task<HookInstallationResult> UninstallHooksAsync(string repoPath, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Uninstalling all hooks for repository: {RepoPath}", repoPath);

            var preCommitResult = await UninstallPreCommitHookAsync(repoPath, cancellationToken);
            var commitMsgResult = await UninstallCommitMsgHookAsync(repoPath, cancellationToken);
            var postCommitResult = await UninstallPostCommitHookAsync(repoPath, cancellationToken);

            if (!preCommitResult.Success || !commitMsgResult.Success || !postCommitResult.Success)
            {
                _logger.LogWarning("Some hooks failed to uninstall");
                return HookInstallationResult.Fail(
                    HookInstallationError.PartialInstallationFailure,
                    "One or more hooks failed to uninstall",
                    $"Pre-commit: {preCommitResult.Success}, Commit-msg: {commitMsgResult.Success}, Post-commit: {postCommitResult.Success}"
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

        private async Task<HookInstallationResult> InstallPostCommitHookAsync(string repoPath, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Installing post-commit hook for repository: {RepoPath}", repoPath);
            return await InstallHookAsync(repoPath, "post-commit", POST_COMMIT_MARKER, "post-commit-hook.sh", cancellationToken);
        }

        private async Task<HookInstallationResult> UninstallPostCommitHookAsync(string repoPath, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Uninstalling post-commit hook for repository: {RepoPath}", repoPath);
            return await UninstallHookAsync(repoPath, "post-commit", POST_COMMIT_MARKER, cancellationToken);
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
                var launchError = _launchCommand.GetValidationError();
                if (launchError != null)
                {
                    return HookInstallationResult.Fail(
                        HookInstallationError.FileWriteError,
                        "Cannot install a hook with an invalid VibeRails launcher",
                        launchError);
                }

                var hooksDir = await ResolveHooksDirectoryAsync(repoPath, cancellationToken);
                if (_installationCheckpoint != null)
                {
                    await _installationCheckpoint(hookName, cancellationToken);
                }

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

                // Handle existing hook files. Only known shell text is ever composed. A
                // symlink, binary, or hook for another interpreter is moved verbatim to a
                // sidecar so installation cannot rewrite its target or corrupt its bytes.
                var sidecarPath = hookPath + CHAINED_HOOK_SUFFIX;
                var chainedHookPath = HookPathExists(sidecarPath)
                    ? sidecarPath
                    : string.Empty;

                if (HookPathExists(hookPath))
                {
                    _logger.LogDebug("Existing hook file found at: {HookPath}", hookPath);
                    var preserveAsSidecar = IsSymbolicLink(hookPath);
                    var existing = string.Empty;
                    if (!preserveAsSidecar)
                    {
                        var existingBytes = await File.ReadAllBytesAsync(hookPath, cancellationToken);
                        preserveAsSidecar = !TryDecodeTextHook(existingBytes, out existing)
                            || !UsesShellInterpreter(existing);
                    }

                    // Remove old Vibe Rails hook if present
                    if (!preserveAsSidecar && existing.Contains(marker, StringComparison.Ordinal))
                    {
                        _logger.LogDebug("Removing existing Vibe Rails hook section");
                        await RemoveHookSection(hookPath, existing, marker, cancellationToken);
                        existing = HookPathExists(hookPath)
                            ? await File.ReadAllTextAsync(hookPath, cancellationToken)
                            : "";
                    }

                    if (!preserveAsSidecar
                        && hookName.Equals("commit-msg", StringComparison.Ordinal)
                        && !string.IsNullOrWhiteSpace(existing))
                    {
                        // Commit-message hooks may edit the message file. Keep them as an
                        // executable sidecar so the VibeRails hook can validate the final
                        // message after those edits have run.
                        preserveAsSidecar = true;
                    }

                    if (preserveAsSidecar)
                    {
                        chainedHookPath = sidecarPath;
                        if (HookPathExists(chainedHookPath))
                        {
                            return HookInstallationResult.Fail(
                                HookInstallationError.FileWriteError,
                                $"Cannot preserve existing {hookName} hook",
                                $"Sidecar already exists: {chainedHookPath}");
                        }

                        File.Move(hookPath, chainedHookPath);
                        existing = string.Empty;
                        _logger.LogInformation(
                            "Preserved non-composable {HookName} hook at {ChainedHookPath}",
                            hookName,
                            chainedHookPath);
                    }

                    // Put VibeRails immediately after the existing shebang. Appending is unsafe:
                    // many third-party hooks end with `exit`, which made our section unreachable.
                    if (!string.IsNullOrWhiteSpace(existing))
                    {
                        _logger.LogDebug("Composing VibeRails before existing hook content");
                        hookContent = ComposeHookContent(hookContent, existing);
                    }
                }

                hookContent = MaterializeHookScript(hookContent, chainedHookPath);

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

                // Verify the exact current hook contract, not merely that a marker exists.
                var installedStatus = InspectHookFile(hookPath, hookName, marker);
                if (!installedStatus.IsCurrent)
                {
                    _logger.LogError(
                        "Hook verification failed for {HookPath}: {Message}",
                        hookPath,
                        installedStatus.Message);
                    return HookInstallationResult.Fail(
                        HookInstallationError.FileWriteError,
                        "Hook failed integrity verification after installation",
                        installedStatus.Message
                    );
                }

                _logger.LogInformation("Successfully installed {HookName} hook", hookName);
                return HookInstallationResult.Ok();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
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
                var hooksPath = await ResolveHooksDirectoryAsync(repoPath, cancellationToken);
                var hookPath = Path.Combine(hooksPath, hookName);

                if (!HookPathExists(hookPath))
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

                var chainedHookPath = hookPath + CHAINED_HOOK_SUFFIX;
                if (HookPathExists(chainedHookPath))
                {
                    if (HookPathExists(hookPath))
                    {
                        return HookInstallationResult.Fail(
                            HookInstallationError.PartialInstallationFailure,
                            $"Removed VibeRails from {hookName}, but could not restore the preserved hook",
                            $"Both {hookPath} and {chainedHookPath} exist");
                    }

                    File.Move(chainedHookPath, hookPath);
                    _logger.LogInformation(
                        "Restored preserved non-shell {HookName} hook from {ChainedHookPath}",
                        hookName,
                        chainedHookPath);
                }

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

                if (IsEmptyOrShebangOnly(newContent))
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
                if (IsEmptyOrShebangOnly(newContent))
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
                var scriptPath = Path.Combine(_scriptsDirectory, scriptFileName);

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

        private async Task<string> ResolveHooksDirectoryAsync(
            string repoPath,
            CancellationToken cancellationToken)
        {
            var fullRepoPath = Path.GetFullPath(repoPath);
            var result = await GitProcessRunner.RunAsync(
                "rev-parse --git-path hooks",
                fullRepoPath,
                GitPathTimeout,
                cancellationToken);

            if (!result.TimedOut && result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.StdOut))
            {
                var reportedPath = result.StdOut.Trim().Trim('"');
                return Path.GetFullPath(
                    Path.IsPathRooted(reportedPath)
                        ? reportedPath
                        : Path.Combine(fullRepoPath, reportedPath));
            }

            // The fallback keeps unit-test fake repositories useful and preserves behavior when
            // Git itself is temporarily unavailable. Real repositories normally take the path
            // above, including linked worktrees and core.hooksPath configurations.
            return Path.Combine(fullRepoPath, ".git", "hooks");
        }

        private GitHookFileStatus InspectHookFile(
            string hookPath,
            string hookName,
            string marker)
        {
            if (!File.Exists(hookPath))
            {
                return new GitHookFileStatus(
                    hookName,
                    hookPath,
                    GitHookFileState.Missing,
                    HasVibeRailsSection: false,
                    "Hook file is missing.");
            }

            try
            {
                var content = File.ReadAllText(hookPath);
                if (!content.Contains(marker, StringComparison.Ordinal))
                {
                    return new GitHookFileStatus(
                        hookName,
                        hookPath,
                        GitHookFileState.Missing,
                        HasVibeRailsSection: false,
                        "Hook file exists, but it has no VibeRails section.");
                }

                if (ManagedSectionContains(content, marker, DISABLED_MARKER))
                {
                    return new GitHookFileStatus(
                        hookName,
                        hookPath,
                        GitHookFileState.Disabled,
                        HasVibeRailsSection: true,
                        "The installed VibeRails hook is disabled.");
                }

                if (!OperatingSystem.IsWindows())
                {
                    var mode = File.GetUnixFileMode(hookPath);
                    var executableBits = UnixFileMode.UserExecute
                        | UnixFileMode.GroupExecute
                        | UnixFileMode.OtherExecute;
                    if ((mode & executableBits) == 0)
                    {
                        return new GitHookFileStatus(
                            hookName,
                            hookPath,
                            GitHookFileState.Stale,
                            HasVibeRailsSection: true,
                            "The installed hook is not executable.");
                    }
                }

                var expectedInvocation = hookName switch
                {
                    "commit-msg" => "--vca-hook commit-msg",
                    "post-commit" => "--job-trigger post-commit",
                    _ => "--vca-hook pre-commit"
                };
                var launchError = _launchCommand.GetValidationError();
                if (launchError != null)
                {
                    return new GitHookFileStatus(
                        hookName,
                        hookPath,
                        GitHookFileState.Stale,
                        HasVibeRailsSection: true,
                        $"The VibeRails launcher is unavailable: {launchError}");
                }

                var expectedExecutable = $"VIBERAILS_EXECUTABLE='{EscapeForSingleQuotedShell(_launchCommand.Executable)}'";
                var expectedArgument = $"VIBERAILS_EXECUTABLE_ARGUMENT='{EscapeForSingleQuotedShell(_launchCommand.Argument)}'";
                var expectedVersionMarker = VERSION_MARKER_PREFIX + _hookVersion;
                var isCurrent = ManagedSectionContainsExactLine(content, marker, expectedVersionMarker)
                    && content.Contains(END_MARKER, StringComparison.Ordinal)
                    && content.Contains(expectedInvocation, StringComparison.Ordinal)
                    && content.Contains(expectedExecutable, StringComparison.Ordinal)
                    && content.Contains(expectedArgument, StringComparison.Ordinal);

                return isCurrent
                    ? new GitHookFileStatus(
                        hookName,
                        hookPath,
                        GitHookFileState.Current,
                        HasVibeRailsSection: true,
                        $"Hook is active for VibeRails {_hookVersion}.")
                    : new GitHookFileStatus(
                        hookName,
                        hookPath,
                        GitHookFileState.Stale,
                        HasVibeRailsSection: true,
                        $"The installed VibeRails hook is stale or incomplete; expected app version {_hookVersion}.");
            }
            catch (Exception ex)
            {
                return new GitHookFileStatus(
                    hookName,
                    hookPath,
                    GitHookFileState.Stale,
                    HasVibeRailsSection: true,
                    $"Hook could not be inspected: {ex.Message}");
            }
        }

        private static bool IsEmptyOrShebangOnly(string content)
        {
            var trimmed = content.Trim();
            return trimmed.Length == 0
                || (trimmed.StartsWith("#!", StringComparison.Ordinal)
                    && !trimmed.Contains('\r')
                    && !trimmed.Contains('\n'));
        }

        private static bool ManagedSectionContains(string content, string marker, string value)
        {
            var startIndex = content.IndexOf(marker, StringComparison.Ordinal);
            if (startIndex < 0)
                return false;

            var endIndex = content.IndexOf(END_MARKER, startIndex, StringComparison.Ordinal);
            var count = endIndex < 0
                ? content.Length - startIndex
                : endIndex + END_MARKER.Length - startIndex;
            return content.IndexOf(value, startIndex, count, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool ManagedSectionContainsExactLine(
            string content,
            string marker,
            string expectedLine)
        {
            var startIndex = content.IndexOf(marker, StringComparison.Ordinal);
            if (startIndex < 0)
            {
                return false;
            }

            var endIndex = content.IndexOf(END_MARKER, startIndex, StringComparison.Ordinal);
            var count = endIndex < 0
                ? content.Length - startIndex
                : endIndex + END_MARKER.Length - startIndex;
            return content.Substring(startIndex, count)
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Split('\n')
                .Any(line => line.TrimEnd('\r').Equals(expectedLine, StringComparison.Ordinal));
        }

        private string MaterializeHookScript(string hookContent, string chainedHookPath)
        {
            return hookContent
                .Replace(VERSION_PLACEHOLDER, _hookVersion, StringComparison.Ordinal)
                .Replace(EXECUTABLE_PLACEHOLDER, EscapeForSingleQuotedShell(_launchCommand.Executable), StringComparison.Ordinal)
                .Replace(EXECUTABLE_ARGUMENT_PLACEHOLDER, EscapeForSingleQuotedShell(_launchCommand.Argument), StringComparison.Ordinal)
                .Replace(CHAINED_HOOK_PLACEHOLDER, EscapeForSingleQuotedShell(chainedHookPath), StringComparison.Ordinal);
        }

        /// <summary>
        /// Constrains the version to SemVer-safe characters before it is substituted into the hook
        /// script. This is a shell-injection guard, not cosmetics: <see cref="MaterializeHookScript"/>
        /// escapes every other placeholder with <see cref="EscapeForSingleQuotedShell"/> and this one
        /// is inserted raw, so the character set IS the escaping.
        ///
        /// A rejected value falls back rather than throwing. The version reaches here from the built
        /// application metadata, so throwing would put a malformed build version in the path of a
        /// constructor — turning a cosmetic typo into a failed service resolution.
        /// </summary>
        private const string FallbackHookVersion = "0.0.0";

        private static bool TryNormalizeHookVersion(string version, out string normalized)
        {
            normalized = version.Trim();
            if (normalized.Length is 0 or > 64
                || normalized.Any(character =>
                    !char.IsAsciiLetterOrDigit(character)
                    && character is not '.' and not '-' and not '+'))
            {
                normalized = FallbackHookVersion;
                return false;
            }

            return true;
        }

        private static HookLaunchCommand GetCurrentLaunchCommand()
        {
            return ResolveLaunchCommand(
                Environment.ProcessPath,
                Environment.GetCommandLineArgs());
        }

        internal static HookLaunchCommand ResolveLaunchCommand(
            string? processPath,
            IReadOnlyList<string> commandLine)
        {
            var executable = string.IsNullOrWhiteSpace(processPath)
                ? string.Empty
                : Path.GetFullPath(processPath);
            if (!IsDotNetHost(executable))
            {
                return new HookLaunchCommand(executable, string.Empty);
            }

            // Under a framework-dependent launch ProcessPath is dotnet (or dotnet.exe),
            // while the entry assembly is the command the hook must pass back to the host.
            // Environment.GetCommandLineArgs()[0] is the managed entry assembly for a
            // framework-dependent application, even though ProcessPath is the dotnet host.
            var entryDll = commandLine.Count > 0
                && commandLine[0].EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                    ? commandLine[0]
                    : string.Empty;

            return new HookLaunchCommand(
                executable,
                string.IsNullOrWhiteSpace(entryDll) ? string.Empty : Path.GetFullPath(entryDll));
        }

        private static bool IsDotNetHost(string executable) =>
            Path.GetFileNameWithoutExtension(executable)
                .Equals("dotnet", StringComparison.OrdinalIgnoreCase);

        private static string EscapeForSingleQuotedShell(string value) =>
            value.Replace("'", "'\"'\"'", StringComparison.Ordinal);

        private static string ComposeHookContent(string vibeRailsHook, string existingHook)
        {
            var normalizedVibe = vibeRailsHook.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
            var normalizedExisting = existingHook.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();

            if (!normalizedExisting.StartsWith("#!", StringComparison.Ordinal))
            {
                return normalizedVibe + "\n\n" + normalizedExisting + "\n";
            }

            var firstNewline = normalizedExisting.IndexOf('\n');
            if (firstNewline < 0)
            {
                return normalizedExisting + "\n" + RemoveShebang(normalizedVibe) + "\n";
            }

            var shebang = normalizedExisting[..firstNewline];
            var existingBody = normalizedExisting[(firstNewline + 1)..].Trim();
            var vibeBody = RemoveShebang(normalizedVibe);
            return string.IsNullOrWhiteSpace(existingBody)
                ? shebang + "\n" + vibeBody + "\n"
                : shebang + "\n" + vibeBody + "\n\n" + existingBody + "\n";
        }

        private static string RemoveShebang(string content)
        {
            if (!content.StartsWith("#!", StringComparison.Ordinal))
            {
                return content.Trim();
            }

            var firstNewline = content.IndexOf('\n');
            return firstNewline < 0 ? string.Empty : content[(firstNewline + 1)..].Trim();
        }

        private static bool UsesShellInterpreter(string content)
        {
            var normalized = content.Replace("\r\n", "\n", StringComparison.Ordinal).TrimStart();
            if (!normalized.StartsWith("#!", StringComparison.Ordinal))
            {
                return false;
            }

            var firstLine = normalized.Split('\n', 2)[0];
            return firstLine.Contains("/sh", StringComparison.OrdinalIgnoreCase)
                || firstLine.Contains("bash", StringComparison.OrdinalIgnoreCase)
                || firstLine.Contains("dash", StringComparison.OrdinalIgnoreCase)
                || firstLine.Contains("zsh", StringComparison.OrdinalIgnoreCase)
                || firstLine.Contains("ksh", StringComparison.OrdinalIgnoreCase)
                || firstLine.EndsWith(" sh", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryDecodeTextHook(byte[] content, out string text)
        {
            if (content.AsSpan().Contains((byte)0))
            {
                text = string.Empty;
                return false;
            }

            try
            {
                text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                    .GetString(content);
                return true;
            }
            catch (DecoderFallbackException)
            {
                text = string.Empty;
                return false;
            }
        }

        private static bool HookPathExists(string path) =>
            File.Exists(path) || GetSymbolicLinkTarget(path) != null;

        private static bool IsSymbolicLink(string path) =>
            GetSymbolicLinkTarget(path) != null;

        private static string? GetSymbolicLinkTarget(string path)
        {
            try
            {
                return new FileInfo(path).LinkTarget;
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }

        private static async Task<List<HookFileSnapshot>> CaptureSnapshotsAsync(
            string hooksPath,
            CancellationToken cancellationToken)
        {
            var snapshots = new List<HookFileSnapshot>();
            foreach (var name in new[]
                     {
                         "pre-commit",
                         "pre-commit" + CHAINED_HOOK_SUFFIX,
                         "commit-msg",
                         "commit-msg" + CHAINED_HOOK_SUFFIX,
                         "post-commit",
                         "post-commit" + CHAINED_HOOK_SUFFIX
                     })
            {
                var path = Path.Combine(hooksPath, name);
                var linkTarget = GetSymbolicLinkTarget(path);
                if (linkTarget != null)
                {
                    snapshots.Add(new HookFileSnapshot(
                        path,
                        HookFileSnapshotKind.SymbolicLink,
                        [],
                        null,
                        null,
                        linkTarget));
                    continue;
                }

                if (!File.Exists(path))
                {
                    snapshots.Add(new HookFileSnapshot(
                        path,
                        HookFileSnapshotKind.Missing,
                        [],
                        null,
                        null,
                        null));
                    continue;
                }

                var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
                var attributes = File.GetAttributes(path);
                UnixFileMode? unixMode = OperatingSystem.IsWindows()
                    ? null
                    : File.GetUnixFileMode(path);
                snapshots.Add(new HookFileSnapshot(
                    path,
                    HookFileSnapshotKind.RegularFile,
                    bytes,
                    attributes,
                    unixMode,
                    null));
            }

            return snapshots;
        }

        private async Task<HookRollbackResult> RestoreSnapshotsAsync(
            IReadOnlyList<HookFileSnapshot> snapshots,
            CancellationToken cancellationToken)
        {
            var failures = new List<HookRollbackFailure>();
            foreach (var snapshot in snapshots)
            {
                try
                {
                    if (snapshot.Kind == HookFileSnapshotKind.Missing)
                    {
                        if (ShouldPreserveSidecarAfterPrimaryRestoreFailure(snapshot.Path, failures))
                        {
                            _logger.LogWarning(
                                "Preserving sidecar {HookPath} because the primary hook restore failed",
                                snapshot.Path);
                            continue;
                        }

                        DeleteHookPath(snapshot.Path);
                        continue;
                    }

                    if (snapshot.Kind == HookFileSnapshotKind.SymbolicLink)
                    {
                        var currentTarget = GetSymbolicLinkTarget(snapshot.Path);
                        if (string.Equals(currentTarget, snapshot.LinkTarget, StringComparison.Ordinal))
                        {
                            continue;
                        }

                        DeleteHookPath(snapshot.Path);
                        Directory.CreateDirectory(Path.GetDirectoryName(snapshot.Path)!);
                        File.CreateSymbolicLink(snapshot.Path, snapshot.LinkTarget!);
                        continue;
                    }

                    if (GetSymbolicLinkTarget(snapshot.Path) != null)
                    {
                        File.Delete(snapshot.Path);
                        Directory.CreateDirectory(Path.GetDirectoryName(snapshot.Path)!);
                    }
                    else if (File.Exists(snapshot.Path))
                    {
                        var current = await File.ReadAllBytesAsync(snapshot.Path, cancellationToken);
                        if (current.AsSpan().SequenceEqual(snapshot.Content))
                        {
                            if (snapshot.Attributes.HasValue)
                            {
                                File.SetAttributes(snapshot.Path, snapshot.Attributes.Value);
                            }
                            if (snapshot.UnixMode.HasValue && !OperatingSystem.IsWindows())
                            {
                                File.SetUnixFileMode(snapshot.Path, snapshot.UnixMode.Value);
                            }
                            continue;
                        }

                        File.SetAttributes(snapshot.Path, FileAttributes.Normal);
                    }
                    else
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(snapshot.Path)!);
                    }

                    await File.WriteAllBytesAsync(snapshot.Path, snapshot.Content, cancellationToken);
                    if (snapshot.Attributes.HasValue)
                    {
                        File.SetAttributes(snapshot.Path, snapshot.Attributes.Value);
                    }
                    if (snapshot.UnixMode.HasValue && !OperatingSystem.IsWindows())
                    {
                        File.SetUnixFileMode(snapshot.Path, snapshot.UnixMode.Value);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to restore hook snapshot {HookPath}", snapshot.Path);
                    failures.Add(new HookRollbackFailure(snapshot.Path, ex));
                }
            }

            return new HookRollbackResult(failures);
        }

        private static bool ShouldPreserveSidecarAfterPrimaryRestoreFailure(
            string path,
            IReadOnlyList<HookRollbackFailure> failures)
        {
            if (!path.EndsWith(CHAINED_HOOK_SUFFIX, StringComparison.Ordinal))
            {
                return false;
            }

            var primaryPath = path[..^CHAINED_HOOK_SUFFIX.Length];
            return failures.Any(failure => PathsEqual(failure.Path, primaryPath));
        }

        private static bool PathsEqual(string left, string right) =>
            string.Equals(
                Path.GetFullPath(left),
                Path.GetFullPath(right),
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal);

        private static void DeleteHookPath(string path)
        {
            if (GetSymbolicLinkTarget(path) != null)
            {
                File.Delete(path);
                return;
            }

            if (File.Exists(path))
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
        }

        private enum HookFileSnapshotKind
        {
            Missing,
            RegularFile,
            SymbolicLink
        }

        private sealed record HookFileSnapshot(
            string Path,
            HookFileSnapshotKind Kind,
            byte[] Content,
            FileAttributes? Attributes,
            UnixFileMode? UnixMode,
            string? LinkTarget);

        private sealed record HookRollbackFailure(string Path, Exception Exception);

        private sealed record HookRollbackResult(IReadOnlyList<HookRollbackFailure> Failures)
        {
            internal bool Success => Failures.Count == 0;

            internal string DescribeFailures() => string.Join(
                "; ",
                Failures.Select(failure => $"{failure.Path}: {failure.Exception.Message}"));
        }

        internal sealed record HookLaunchCommand(string Executable, string Argument)
        {
            internal string? GetValidationError()
            {
                if (string.IsNullOrWhiteSpace(Executable))
                {
                    return "The current VibeRails executable path is empty.";
                }

                if (!File.Exists(Executable))
                {
                    return $"The current VibeRails executable does not exist: {Executable}";
                }

                if (IsDotNetHost(Executable) && string.IsNullOrWhiteSpace(Argument))
                {
                    return "The current dotnet launch is missing its VibeRails entry DLL.";
                }

                if (!string.IsNullOrWhiteSpace(Argument) && !File.Exists(Argument))
                {
                    return $"The VibeRails launch argument does not exist: {Argument}";
                }

                return null;
            }
        }

        private async Task<bool> MakeExecutableAsync(string path, CancellationToken cancellationToken)
        {
            try
            {
                var process = new System.Diagnostics.Process
                {
                    StartInfo = CreateChmodStartInfo(path)
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

        internal static System.Diagnostics.ProcessStartInfo CreateChmodStartInfo(string path)
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "chmod",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("+x");
            startInfo.ArgumentList.Add(path);
            return startInfo;
        }
    }
}
