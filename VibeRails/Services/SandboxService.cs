using System.Diagnostics;
using System.Text.RegularExpressions;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Utils;

namespace VibeRails.Services
{
    public interface ISandboxService
    {
        Task<Sandbox> CreateSandboxAsync(string name, string projectPath, CancellationToken ct = default);
        Task DeleteSandboxAsync(int sandboxId, CancellationToken ct = default);
        Task<List<Sandbox>> GetSandboxesAsync(string projectPath, CancellationToken ct = default);
        Task<SandboxDiffResult> GetDiffAsync(int sandboxId, CancellationToken ct = default);
        Task<string> PushToRemoteAsync(int sandboxId, CancellationToken ct = default);
        Task<string> MergeLocallyAsync(int sandboxId, CancellationToken ct = default);
    }

    public class SandboxDiffFile
    {
        public string FileName { get; set; } = "";
        public string Language { get; set; } = "plaintext";
        public string OriginalContent { get; set; } = "";
        public string ModifiedContent { get; set; } = "";
    }

    public class SandboxDiffResult
    {
        public List<SandboxDiffFile> Files { get; set; } = new();
        public int TotalChanges { get; set; }
    }

    public class SandboxService : ISandboxService
    {
        private readonly IRepository _repository;

        private static readonly Regex ValidNameRegex = new(@"^[a-zA-Z0-9_-]+$", RegexOptions.Compiled);

        public SandboxService(IRepository repository)
        {
            _repository = repository;
        }

        public async Task<Sandbox> CreateSandboxAsync(string name, string projectPath, CancellationToken ct = default)
        {
            // Validate name
            if (string.IsNullOrWhiteSpace(name))
                throw new InvalidOperationException("Sandbox name is required.");

            if (!ValidNameRegex.IsMatch(name))
                throw new InvalidOperationException("Sandbox name can only contain alphanumeric characters, hyphens, and underscores.");

            // Check for duplicate
            var existing = await _repository.GetSandboxByNameAndProjectAsync(name, projectPath, ct);
            if (existing != null)
                throw new InvalidOperationException($"A sandbox named '{name}' already exists for this project.");

            // Compute sandbox path (global sandboxes dir)
            var sandboxBasePath = ParserConfigs.GetSandboxPath();
            var sandboxPath = Path.Combine(sandboxBasePath, name);

            if (Directory.Exists(sandboxPath))
                throw new InvalidOperationException($"Directory already exists at '{sandboxPath}'. Choose a different name.");

            // Get current branch and commit hash from source project
            var gitService = new GitService(projectPath);
            var branch = await gitService.GetCurrentBranchAsync(ct) ?? "main";
            var commitHash = await gitService.GetCurrentCommitHashAsync(ct);

            // Shallow clone from local repo
            await RunGitCloneAsync(projectPath, sandboxPath, branch, ct);

            // Get dirty + untracked files from source and copy them over
            await CopyDirtyFilesAsync(projectPath, sandboxPath, ct);

            // If the source project has a real remote, point the sandbox at it
            // (the clone's origin currently points at the local filesystem path)
            var remoteUrl = await SetupSandboxRemoteAsync(projectPath, sandboxPath, ct);

            // Create and checkout a sandbox-specific branch
            var sandboxBranch = $"sandbox/{name}";
            await RunGitCommandAsync(sandboxPath, $"checkout -b \"{sandboxBranch}\"", ct);

            // Save to DB
            var sandbox = new Sandbox
            {
                Name = name,
                Path = sandboxPath,
                ProjectPath = projectPath,
                Branch = sandboxBranch,
                SourceBranch = branch,
                CommitHash = commitHash,
                RemoteUrl = remoteUrl,
                CreatedUTC = DateTime.UtcNow
            };

            return await _repository.SaveSandboxAsync(sandbox, ct);
        }

        public async Task DeleteSandboxAsync(int sandboxId, CancellationToken ct = default)
        {
            var sandbox = await _repository.GetSandboxByIdAsync(sandboxId, ct);
            if (sandbox == null)
                throw new InvalidOperationException("Sandbox not found.");

            // Delete the directory if it exists
            if (Directory.Exists(sandbox.Path))
            {
                // Git marks objects as read-only; clear those flags so Directory.Delete works on Windows.
                // On Linux this is a harmless no-op for most files.
                ClearReadOnlyAttributes(sandbox.Path);
                Directory.Delete(sandbox.Path, recursive: true);
            }

            await _repository.DeleteSandboxAsync(sandboxId, ct);
        }

        public async Task<List<Sandbox>> GetSandboxesAsync(string projectPath, CancellationToken ct = default)
        {
            return await _repository.GetSandboxesByProjectAsync(projectPath, ct);
        }

        public async Task<SandboxDiffResult> GetDiffAsync(int sandboxId, CancellationToken ct = default)
        {
            var sandbox = await _repository.GetSandboxByIdAsync(sandboxId, ct);
            if (sandbox == null)
                throw new InvalidOperationException("Sandbox not found.");

            if (!Directory.Exists(sandbox.Path))
                throw new InvalidOperationException("Sandbox directory no longer exists.");

            var files = new List<SandboxDiffFile>();

            // Get committed changes since the original commit
            var changedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrWhiteSpace(sandbox.CommitHash))
            {
                var diffOutput = await RunGitCommandAsync(sandbox.Path,
                    $"diff --name-only {sandbox.CommitHash}..HEAD", ct);
                if (!string.IsNullOrWhiteSpace(diffOutput))
                {
                    foreach (var line in diffOutput.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
                        changedFiles.Add(line.Trim());
                }
            }

            // Also get uncommitted changes
            var statusOutput = await RunGitCommandAsync(sandbox.Path,
                "status --porcelain=v1 --untracked-files=all --ignore-submodules", ct);
            if (!string.IsNullOrWhiteSpace(statusOutput))
            {
                foreach (var line in statusOutput.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (line.Length < 4) continue;
                    var filePath = line.Substring(3).Trim();
                    var arrowIndex = filePath.IndexOf("->", StringComparison.Ordinal);
                    if (arrowIndex >= 0)
                        filePath = filePath.Substring(arrowIndex + 2).Trim();
                    changedFiles.Add(filePath.Replace('\\', '/'));
                }
            }

            foreach (var filePath in changedFiles.OrderBy(f => f))
            {
                // Get original content from the base commit
                var originalContent = "";
                if (!string.IsNullOrWhiteSpace(sandbox.CommitHash))
                {
                    try
                    {
                        originalContent = await RunGitCommandAsync(sandbox.Path,
                            $"show {sandbox.CommitHash}:\"{filePath}\"", ct);
                    }
                    catch
                    {
                        // File didn't exist at the base commit (new file)
                    }
                }

                // Get modified content from disk
                var modifiedContent = "";
                var fullPath = Path.Combine(sandbox.Path, filePath);
                if (File.Exists(fullPath))
                {
                    modifiedContent = await File.ReadAllTextAsync(fullPath, ct);
                }

                files.Add(new SandboxDiffFile
                {
                    FileName = filePath,
                    Language = GetLanguageFromExtension(filePath),
                    OriginalContent = originalContent,
                    ModifiedContent = modifiedContent
                });
            }

            return new SandboxDiffResult { Files = files, TotalChanges = files.Count };
        }

        public async Task<string> PushToRemoteAsync(int sandboxId, CancellationToken ct = default)
        {
            var sandbox = await _repository.GetSandboxByIdAsync(sandboxId, ct);
            if (sandbox == null)
                throw new InvalidOperationException("Sandbox not found.");

            if (string.IsNullOrWhiteSpace(sandbox.RemoteUrl))
                throw new InvalidOperationException("Cannot push: sandbox has no remote URL configured.");

            if (!Directory.Exists(sandbox.Path))
                throw new InvalidOperationException("Sandbox directory no longer exists.");

            // Check for uncommitted changes
            var status = await RunGitCommandAsync(sandbox.Path, "status --porcelain", ct);
            if (!string.IsNullOrWhiteSpace(status))
                throw new InvalidOperationException("Sandbox has uncommitted changes. Please commit or stash them before pushing.");

            await RunGitCommandAsync(sandbox.Path, $"push -u origin \"{sandbox.Branch}\"", throwOnError: true, ct);

            return $"Branch '{sandbox.Branch}' pushed to remote successfully.";
        }

        public async Task<string> MergeLocallyAsync(int sandboxId, CancellationToken ct = default)
        {
            var sandbox = await _repository.GetSandboxByIdAsync(sandboxId, ct);
            if (sandbox == null)
                throw new InvalidOperationException("Sandbox not found.");

            if (!Directory.Exists(sandbox.Path))
                throw new InvalidOperationException("Sandbox directory no longer exists.");

            if (!Directory.Exists(sandbox.ProjectPath))
                throw new InvalidOperationException("Source project directory no longer exists.");

            // Check sandbox has committed changes
            var sandboxStatus = await RunGitCommandAsync(sandbox.Path, "status --porcelain", ct);
            if (!string.IsNullOrWhiteSpace(sandboxStatus))
                throw new InvalidOperationException("Sandbox has uncommitted changes. Please commit or stash them before merging.");

            // Check source project is clean
            var sourceStatus = await RunGitCommandAsync(sandbox.ProjectPath, "status --porcelain", ct);
            if (!string.IsNullOrWhiteSpace(sourceStatus))
                throw new InvalidOperationException("Source project has uncommitted changes. Please commit or stash them before merging.");

            // Check source project is on the expected branch
            var sourceBranch = sandbox.SourceBranch ?? "main";
            var sourceGit = new GitService(sandbox.ProjectPath);
            var currentSourceBranch = await sourceGit.GetCurrentBranchAsync(ct);
            if (currentSourceBranch != sourceBranch)
                throw new InvalidOperationException(
                    $"Source project is on branch '{currentSourceBranch}', but sandbox was created from '{sourceBranch}'. " +
                    $"Please checkout '{sourceBranch}' in the source project first.");

            var remoteName = $"sandbox-{sandbox.Name}";

            try
            {
                await RunGitCommandAsync(sandbox.ProjectPath,
                    $"remote add \"{remoteName}\" \"{sandbox.Path}\"", throwOnError: true, ct);

                await RunGitCommandAsync(sandbox.ProjectPath,
                    $"fetch \"{remoteName}\"", throwOnError: true, ct);

                await RunGitCommandAsync(sandbox.ProjectPath,
                    $"merge \"{remoteName}/{sandbox.Branch}\" --no-edit", throwOnError: true, ct);

                return $"Sandbox '{sandbox.Name}' merged into '{sourceBranch}' successfully.";
            }
            catch (Exception ex) when (ex.Message.Contains("CONFLICT", StringComparison.OrdinalIgnoreCase) ||
                                       ex.Message.Contains("merge", StringComparison.OrdinalIgnoreCase))
            {
                try { await RunGitCommandAsync(sandbox.ProjectPath, "merge --abort", ct); }
                catch { /* best effort abort */ }

                throw new InvalidOperationException(
                    "Merge conflict detected. The merge has been aborted. " +
                    "You may need to manually merge the changes.");
            }
            finally
            {
                try { await RunGitCommandAsync(sandbox.ProjectPath, $"remote remove \"{remoteName}\"", ct); }
                catch { /* best effort cleanup */ }
            }
        }

        private static string GetLanguageFromExtension(string filePath)
        {
            var ext = Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant();
            return ext switch
            {
                "js" => "javascript",
                "ts" => "typescript",
                "tsx" => "typescript",
                "jsx" => "javascript",
                "py" => "python",
                "cs" => "csharp",
                "css" => "css",
                "html" or "htm" => "html",
                "json" => "json",
                "md" => "markdown",
                "xml" => "xml",
                "yaml" or "yml" => "yaml",
                "sh" or "bash" => "shell",
                "sql" => "sql",
                "rs" => "rust",
                "go" => "go",
                "java" => "java",
                "rb" => "ruby",
                "php" => "php",
                "cpp" or "cc" or "cxx" => "cpp",
                "c" or "h" => "c",
                "swift" => "swift",
                _ => "plaintext"
            };
        }

        /// <summary>
        /// Gets the real remote URL from the source project and sets it in the sandbox.
        /// If the source has no remote, removes the local-path origin from the sandbox.
        /// Returns the remote URL if one was found, null otherwise.
        /// </summary>
        private static async Task<string?> SetupSandboxRemoteAsync(string projectPath, string sandboxPath, CancellationToken ct)
        {
            // Get the real remote URL from the source project
            string? remoteUrl = null;
            try
            {
                remoteUrl = await RunGitCommandAsync(projectPath, "remote get-url origin", ct);
            }
            catch
            {
                // No remote configured in source — that's fine
            }

            if (!string.IsNullOrWhiteSpace(remoteUrl))
            {
                // Source has a real remote — point the sandbox at it
                await RunGitCommandAsync(sandboxPath, $"remote set-url origin \"{remoteUrl}\"", ct);
                return remoteUrl;
            }
            else
            {
                // Source has no remote — remove the local-path origin from the sandbox
                try
                {
                    await RunGitCommandAsync(sandboxPath, "remote remove origin", ct);
                }
                catch
                {
                    // Ignore if origin doesn't exist
                }
                return null;
            }
        }

        private static async Task RunGitCloneAsync(string sourcePath, string destPath, string branch, CancellationToken ct)
        {
            var arguments = $"clone --depth 1 --branch \"{branch}\" --single-branch \"{sourcePath}\" \"{destPath}\"";

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();

            var outputTask = process.StandardOutput.ReadToEndAsync(ct);
            var errorTask = process.StandardError.ReadToEndAsync(ct);

            await process.WaitForExitAsync(ct);

            var error = await errorTask;

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"Git clone failed: {error}");
            }
        }

        private static async Task CopyDirtyFilesAsync(string projectPath, string sandboxPath, CancellationToken ct)
        {
            // Get all dirty/untracked files via git status --porcelain
            var output = await RunGitCommandAsync(projectPath, "status --porcelain=v1 --untracked-files=all --ignore-submodules", ct);

            if (string.IsNullOrWhiteSpace(output))
                return;

            var lines = output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var line in lines)
            {
                if (line.Length < 4) continue;

                var statusCode = line.Substring(0, 2);
                var filePath = line.Substring(3).Trim();

                // Handle renames (e.g., "R  old -> new")
                var arrowIndex = filePath.IndexOf("->", StringComparison.Ordinal);
                if (arrowIndex >= 0)
                {
                    filePath = filePath.Substring(arrowIndex + 2).Trim();
                }

                // Normalize path separators
                filePath = filePath.Replace('\\', '/');

                // Skip .vibe_rails directory contents
                if (filePath.StartsWith($"{PathConstants.DEFAULT_INSTALL_DIR_NAME}/", StringComparison.OrdinalIgnoreCase))
                    continue;

                var sourceFull = Path.Combine(projectPath, filePath);
                var destFull = Path.Combine(sandboxPath, filePath);

                // Deleted files: remove from sandbox if they exist
                if (statusCode.Contains('D'))
                {
                    if (File.Exists(destFull))
                    {
                        File.Delete(destFull);
                    }
                    continue;
                }

                // For all other statuses: copy the file
                if (File.Exists(sourceFull))
                {
                    var destDir = Path.GetDirectoryName(destFull);
                    if (destDir != null && !Directory.Exists(destDir))
                    {
                        Directory.CreateDirectory(destDir);
                    }
                    File.Copy(sourceFull, destFull, overwrite: true);
                }
            }
        }

        private static void ClearReadOnlyAttributes(string directory)
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                var attrs = File.GetAttributes(file);
                if ((attrs & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(file, attrs & ~FileAttributes.ReadOnly);
            }
        }

        private static async Task<string> RunGitCommandAsync(string workingDirectory, string arguments, CancellationToken ct)
        {
            return await RunGitCommandAsync(workingDirectory, arguments, throwOnError: false, ct);
        }

        private static async Task<string> RunGitCommandAsync(string workingDirectory, string arguments, bool throwOnError, CancellationToken ct)
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = $"--no-pager {arguments}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = workingDirectory
                }
            };

            process.Start();

            var outputTask = process.StandardOutput.ReadToEndAsync(ct);
            var errorTask = process.StandardError.ReadToEndAsync(ct);

            await process.WaitForExitAsync(ct);

            var output = (await outputTask).Trim();
            var error = (await errorTask).Trim();

            if (throwOnError && process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    !string.IsNullOrWhiteSpace(error) ? error : $"Git command failed with exit code {process.ExitCode}");
            }

            return output;
        }
    }
}
