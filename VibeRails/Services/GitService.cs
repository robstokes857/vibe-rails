using VibeRails.DTOs;

namespace VibeRails.Services
{
    public interface IGitService
    {
        Task<List<string>> GetChangedFileAsync(CancellationToken cancellationToken);
        Task<List<string>> GetStagedFilesAsync(CancellationToken cancellationToken);
        Task<string> GetRootPathAsync(CancellationToken cancellationToken = default);
        Task<string?> GetCurrentCommitHashAsync(CancellationToken cancellationToken = default);
        Task<List<FileChangeInfo>> GetFileChangesSinceAsync(string commitHash, CancellationToken cancellationToken = default);
    }

    public class GitService : IGitService
    {
        private readonly string? _workingDirectory;

        public GitService() { }

        public GitService(string workingDirectory)
        {
            _workingDirectory = workingDirectory;
        }

        public async Task<string> GetRootPathAsync(CancellationToken cancellationToken = default)
        {
            var rootPath = await RunGitCommandAsync("rev-parse --show-toplevel", cancellationToken);
            return rootPath;
        }

        public async Task<List<string>> GetChangedFileAsync(CancellationToken cancellationToken)
        {

            var output = await RunGitCommandAsync(
                "status --porcelain=v1 --untracked-files=all --ignore-submodules",
                cancellationToken);

            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var line in SplitLines(output))
            {

                if (line.Length < 4) continue;

                var pathPart = line.Substring(3).Trim();

                // If renamed, keep the new path (right side)
                var arrowIndex = pathPart.IndexOf("->", StringComparison.Ordinal);
                if (arrowIndex >= 0)
                {
                    var newPath = pathPart.Substring(arrowIndex + 2).Trim();
                    if (newPath.Length > 0) files.Add(NormalizePath(newPath));
                }
                else
                {
                    if (pathPart.Length > 0) files.Add(NormalizePath(pathPart));
                }
            }

            return files.ToList();
        }

        public async Task<List<string>> GetStagedFilesAsync(CancellationToken cancellationToken)
        {
            var output = await RunGitCommandAsync(
                "diff --cached --name-only",
                cancellationToken);

            return SplitLines(output)
                .Select(NormalizePath)
                .Where(f => !string.IsNullOrWhiteSpace(f))
                .ToList();
        }

        public async Task<string?> GetCurrentCommitHashAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                var hash = await RunGitCommandAsync("rev-parse HEAD", cancellationToken);
                return string.IsNullOrWhiteSpace(hash) ? null : hash.Trim();
            }
            catch
            {
                return null;
            }
        }

        public async Task<List<FileChangeInfo>> GetFileChangesSinceAsync(string commitHash, CancellationToken cancellationToken = default)
        {
            var changes = new List<FileChangeInfo>();

            try
            {
                // Get file stats (lines added/deleted) since the commit
                // Using diff against the commit to see what changed in working directory
                var numstatOutput = await RunGitCommandAsync(
                    $"diff --numstat {commitHash}",
                    cancellationToken);

                // Also get status to detect new untracked files
                var statusOutput = await RunGitCommandAsync(
                    "status --porcelain=v1 --untracked-files=all",
                    cancellationToken);

                var processedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                // Process numstat output (tracked file changes)
                foreach (var line in SplitLines(numstatOutput))
                {
                    var parts = line.Split('\t');
                    if (parts.Length >= 3)
                    {
                        var added = parts[0] == "-" ? (int?)null : int.TryParse(parts[0], out var a) ? a : null;
                        var deleted = parts[1] == "-" ? (int?)null : int.TryParse(parts[1], out var d) ? d : null;
                        var filePath = NormalizePath(parts[2]);

                        // Determine change type
                        string changeType = "M"; // Modified by default
                        if (added > 0 && deleted == 0) changeType = "A";
                        else if (added == 0 && deleted > 0) changeType = "D";

                        // Get diff content for small files
                        string? diffContent = null;
                        if (added.HasValue && deleted.HasValue && (added.Value + deleted.Value) < 500)
                        {
                            diffContent = await GetFileDiffAsync(commitHash, filePath, cancellationToken);
                        }

                        changes.Add(new FileChangeInfo(
                            FilePath: filePath,
                            ChangeType: changeType,
                            LinesAdded: added,
                            LinesDeleted: deleted,
                            DiffContent: diffContent
                        ));
                        processedFiles.Add(filePath);
                    }
                }

                // Process status output for untracked files (new files not yet in git)
                foreach (var line in SplitLines(statusOutput))
                {
                    if (line.Length < 3) continue;
                    var statusCode = line.Substring(0, 2);
                    var filePath = NormalizePath(line.Substring(3).Trim());

                    // Skip already processed files
                    if (processedFiles.Contains(filePath)) continue;

                    // ?? = untracked (new file)
                    if (statusCode == "??")
                    {
                        changes.Add(new FileChangeInfo(
                            FilePath: filePath,
                            ChangeType: "A",
                            LinesAdded: null,
                            LinesDeleted: null,
                            DiffContent: null
                        ));
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[VibeRails] Error getting file changes: {ex.Message}");
            }

            return changes;
        }

        private async Task<string?> GetFileDiffAsync(string commitHash, string filePath, CancellationToken cancellationToken)
        {
            try
            {
                var diff = await RunGitCommandAsync(
                    $"diff {commitHash} -- \"{filePath}\"",
                    cancellationToken);

                // Limit diff content to 50KB
                if (!string.IsNullOrEmpty(diff) && diff.Length > 50 * 1024)
                {
                    return diff.Substring(0, 50 * 1024) + "\n... [truncated]";
                }
                return string.IsNullOrWhiteSpace(diff) ? null : diff;
            }
            catch
            {
                return null;
            }
        }

        private static string NormalizePath(string path) => path.Replace('\\', '/');

        private static List<string> SplitLines(string s) =>
            string.IsNullOrWhiteSpace(s)
                ? new List<string>()
                : s.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                   .Select(x => x.TrimEnd())
                   .Where(x => x.Length > 0)
                   .ToList();

        private async Task<string> RunGitCommandAsync(string arguments, CancellationToken cancellationToken)
        {
            using var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = $"--no-pager {arguments}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = _workingDirectory ?? Directory.GetCurrentDirectory()
                }
            };

            process.Start();

            // Read both stdout and stderr before waiting
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

            await process.WaitForExitAsync(cancellationToken);

            var output = await outputTask;
            var error = await errorTask;
            return output.Trim();
        }
    }
}
