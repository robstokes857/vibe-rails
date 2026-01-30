namespace VibeRails.Services
{
    public interface IHookInstallationService
    {
        Task<bool> InstallPreCommitHookAsync(string repoPath, CancellationToken cancellationToken);
        Task<bool> UninstallPreCommitHookAsync(string repoPath, CancellationToken cancellationToken);
        Task<bool> InstallHooksAsync(string repoPath, CancellationToken cancellationToken);
        Task<bool> UninstallHooksAsync(string repoPath, CancellationToken cancellationToken);
        bool IsHookInstalled(string repoPath);
    }

    public class HookInstallationService : IHookInstallationService
    {
        private const string PRE_COMMIT_MARKER = "# Vibe Rails Pre-Commit Hook";
        private const string COMMIT_MSG_MARKER = "# Vibe Rails Commit-Msg Hook";
        private const string END_MARKER = "# End Vibe Rails Hook";

        // Keep legacy marker for backwards compatibility
        private const string HOOK_MARKER = "# Vibe Rails Pre-Commit Hook";

        public async Task<bool> InstallPreCommitHookAsync(string repoPath, CancellationToken cancellationToken)
        {
            var hooksDir = Path.Combine(repoPath, ".git", "hooks");
            if (!Directory.Exists(hooksDir))
            {
                return false;
            }

            var hookPath = Path.Combine(hooksDir, "pre-commit");
            var hookContent = GenerateHookScript();

            if (File.Exists(hookPath))
            {
                var existing = await File.ReadAllTextAsync(hookPath, cancellationToken);
                if (existing.Contains(HOOK_MARKER))
                {
                    await RemoveHookSection(hookPath, existing, cancellationToken);
                    existing = File.Exists(hookPath)
                        ? await File.ReadAllTextAsync(hookPath, cancellationToken)
                        : "";
                }

                if (!string.IsNullOrWhiteSpace(existing))
                {
                    hookContent = existing.TrimEnd() + "\n\n" + hookContent;
                }
            }

            await File.WriteAllTextAsync(hookPath, hookContent, cancellationToken);

            if (!OperatingSystem.IsWindows())
            {
                await MakeExecutableAsync(hookPath, cancellationToken);
            }

            return true;
        }

        public async Task<bool> UninstallPreCommitHookAsync(string repoPath, CancellationToken cancellationToken)
        {
            var hookPath = Path.Combine(repoPath, ".git", "hooks", "pre-commit");

            if (!File.Exists(hookPath))
            {
                return true;
            }

            var content = await File.ReadAllTextAsync(hookPath, cancellationToken);
            if (!content.Contains(HOOK_MARKER))
            {
                return true;
            }

            await RemoveHookSection(hookPath, content, cancellationToken);
            return true;
        }

        public bool IsHookInstalled(string repoPath)
        {
            var hookPath = Path.Combine(repoPath, ".git", "hooks", "pre-commit");
            if (!File.Exists(hookPath)) return false;

            var content = File.ReadAllText(hookPath);
            return content.Contains(HOOK_MARKER);
        }

        public async Task<bool> InstallHooksAsync(string repoPath, CancellationToken cancellationToken)
        {
            var preCommitSuccess = await InstallPreCommitHookAsync(repoPath, cancellationToken);
            var commitMsgSuccess = await InstallCommitMsgHookAsync(repoPath, cancellationToken);
            return preCommitSuccess && commitMsgSuccess;
        }

        public async Task<bool> UninstallHooksAsync(string repoPath, CancellationToken cancellationToken)
        {
            var preCommitSuccess = await UninstallPreCommitHookAsync(repoPath, cancellationToken);
            var commitMsgSuccess = await UninstallCommitMsgHookAsync(repoPath, cancellationToken);
            return preCommitSuccess && commitMsgSuccess;
        }

        private async Task<bool> InstallCommitMsgHookAsync(string repoPath, CancellationToken cancellationToken)
        {
            var hooksDir = Path.Combine(repoPath, ".git", "hooks");
            if (!Directory.Exists(hooksDir))
            {
                return false;
            }

            var hookPath = Path.Combine(hooksDir, "commit-msg");
            var hookContent = GenerateCommitMsgHookScript();

            if (File.Exists(hookPath))
            {
                var existing = await File.ReadAllTextAsync(hookPath, cancellationToken);
                if (existing.Contains(COMMIT_MSG_MARKER))
                {
                    await RemoveHookSection(hookPath, existing, COMMIT_MSG_MARKER, cancellationToken);
                    existing = File.Exists(hookPath)
                        ? await File.ReadAllTextAsync(hookPath, cancellationToken)
                        : "";
                }

                if (!string.IsNullOrWhiteSpace(existing))
                {
                    hookContent = existing.TrimEnd() + "\n\n" + hookContent;
                }
            }

            await File.WriteAllTextAsync(hookPath, hookContent, cancellationToken);

            if (!OperatingSystem.IsWindows())
            {
                await MakeExecutableAsync(hookPath, cancellationToken);
            }

            return true;
        }

        private async Task<bool> UninstallCommitMsgHookAsync(string repoPath, CancellationToken cancellationToken)
        {
            var hookPath = Path.Combine(repoPath, ".git", "hooks", "commit-msg");

            if (!File.Exists(hookPath))
            {
                return true;
            }

            var content = await File.ReadAllTextAsync(hookPath, cancellationToken);
            if (!content.Contains(COMMIT_MSG_MARKER))
            {
                return true;
            }

            await RemoveHookSection(hookPath, content, COMMIT_MSG_MARKER, cancellationToken);
            return true;
        }

        private async Task RemoveHookSection(string hookPath, string content, CancellationToken cancellationToken)
        {
            await RemoveHookSection(hookPath, content, HOOK_MARKER, cancellationToken);
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
                    File.Delete(hookPath);
                }
                else
                {
                    await File.WriteAllTextAsync(hookPath, newContent, cancellationToken);
                }
            }
            else if (startIndex >= 0)
            {
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

        private string GenerateHookScript()
        {
            return @"#!/bin/sh
# Vibe Rails Pre-Commit Hook
# Validates VCA rules before commits
# Installed by Vibe Rails - do not edit manually

# Find vb executable
if command -v vb >/dev/null 2>&1; then
    VB_CMD=""vb""
elif [ -f ""./vb"" ]; then
    VB_CMD=""./vb""
elif [ -f ""./vb.exe"" ]; then
    VB_CMD=""./vb.exe""
else
    # Vibe Rails not found - allow commit with warning
    echo ""Warning: vb not found in PATH. Skipping VCA validation.""
    exit 0
fi

# Run VCA validation
$VB_CMD --validate-vca --pre-commit
exit_code=$?

if [ $exit_code -ne 0 ]; then
    echo """"
    echo ""VCA validation failed. Commit blocked.""
    echo ""Fix the issues above or use 'git commit --no-verify' to bypass.""
    exit 1
fi

exit 0
# End Vibe Rails Hook
";
        }

        private string GenerateCommitMsgHookScript()
        {
            return @"#!/bin/sh
# Vibe Rails Commit-Msg Hook
# Validates COMMIT-level acknowledgments in commit message
# Installed by Vibe Rails - do not edit manually

# Find vb executable
if command -v vb >/dev/null 2>&1; then
    VB_CMD=""vb""
elif [ -f ""./vb"" ]; then
    VB_CMD=""./vb""
elif [ -f ""./vb.exe"" ]; then
    VB_CMD=""./vb.exe""
else
    # Vibe Rails not found - allow commit
    exit 0
fi

# Run commit-msg validation
$VB_CMD --commit-msg ""$1""
exit_code=$?

if [ $exit_code -ne 0 ]; then
    echo """"
    echo ""Commit message missing required VCA acknowledgments.""
    echo ""Add acknowledgments for COMMIT-level violations.""
    exit 1
fi

exit 0
# End Vibe Rails Hook
";
        }

        private async Task MakeExecutableAsync(string path, CancellationToken cancellationToken)
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
        }
    }
}
