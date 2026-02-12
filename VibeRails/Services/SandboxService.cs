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
            var sandboxBasePath = Configs.GetSandboxPath();
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

            // Save to DB
            var sandbox = new Sandbox
            {
                Name = name,
                Path = sandboxPath,
                ProjectPath = projectPath,
                Branch = branch,
                CommitHash = commitHash,
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
                Directory.Delete(sandbox.Path, recursive: true);
            }

            await _repository.DeleteSandboxAsync(sandboxId, ct);
        }

        public async Task<List<Sandbox>> GetSandboxesAsync(string projectPath, CancellationToken ct = default)
        {
            return await _repository.GetSandboxesByProjectAsync(projectPath, ct);
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
                if (filePath.StartsWith(".vibe_rails/", StringComparison.OrdinalIgnoreCase))
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

        private static async Task<string> RunGitCommandAsync(string workingDirectory, string arguments, CancellationToken ct)
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

            return (await outputTask).Trim();
        }
    }
}
