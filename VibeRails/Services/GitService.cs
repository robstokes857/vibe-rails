using Serilog;
using VibeRails.DTOs;

namespace VibeRails.Services
{
    public interface IGitService
    {
        Task<List<string>> GetChangedFileAsync(CancellationToken cancellationToken);
        Task<List<string>> GetStagedFilesAsync(CancellationToken cancellationToken);
        Task StageFileAsync(string path, CancellationToken cancellationToken = default);

        /// <summary>
        /// True when staging <paramref name="path"/> would stage only the caller's own edit.
        ///
        /// <c>git add</c> stages a whole file, so it is only safe when the index and the
        /// working tree already agree. Otherwise it would replace a partially staged version
        /// of the file, or sweep unrelated unstaged edits into the index alongside the
        /// caller's change.
        ///
        /// Must be called <em>before</em> the edit: afterwards the caller's own change is
        /// itself an unstaged difference and this would always be false. Returns false for
        /// anything that makes staging impossible — no repository, a path outside it, or Git
        /// being unavailable — so callers can treat it as a single "safe to stage" signal.
        /// </summary>
        Task<bool> IsStagingSafeAsync(string path, CancellationToken cancellationToken = default);
        Task<string> GetRootPathAsync(CancellationToken cancellationToken = default);
        Task<string?> GetCurrentCommitHashAsync(CancellationToken cancellationToken = default);
        Task<string?> GetCurrentBranchAsync(CancellationToken cancellationToken = default);
        Task<string?> GetRemoteUrlAsync(CancellationToken cancellationToken = default);
        Task<List<FileChangeInfo>> GetFileChangesSinceAsync(string commitHash, CancellationToken cancellationToken = default);
    }

    public class GitService : IGitService
    {
        private static readonly TimeSpan RootCommandTimeout = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan StatusCommandTimeout = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan DiffCommandTimeout = TimeSpan.FromSeconds(15);
        private readonly string? _workingDirectory;

        public GitService() { }

        public GitService(string workingDirectory)
        {
            _workingDirectory = workingDirectory;
        }

        public async Task<string> GetRootPathAsync(CancellationToken cancellationToken = default)
        {
            var rootPath = await RunGitCommandAsync("rev-parse --show-toplevel", RootCommandTimeout, cancellationToken);
            return rootPath;
        }

        public async Task<string?> GetRemoteUrlAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                var remote = await RunGitCommandAsync("remote get-url origin", RootCommandTimeout, cancellationToken);
                return string.IsNullOrWhiteSpace(remote) ? null : remote.Trim();
            }
            catch
            {
                return null;
            }
        }

        public async Task<List<string>> GetChangedFileAsync(CancellationToken cancellationToken)
        {

            var output = await RunGitCommandAsync(
                "status --porcelain=v1 --untracked-files=all --ignore-submodules",
                StatusCommandTimeout,
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
                StatusCommandTimeout,
                cancellationToken);

            return SplitLines(output)
                .Select(NormalizePath)
                .Where(f => !string.IsNullOrWhiteSpace(f))
                .ToList();
        }

        public async Task StageFileAsync(string path, CancellationToken cancellationToken = default)
        {
            var (rootPath, relativePath) = await ResolveRepositoryFileAsync(path, cancellationToken);

            var result = await GitProcessRunner.RunAsync(
                ["add", "--", relativePath],
                rootPath,
                StatusCommandTimeout,
                cancellationToken);

            if (result.TimedOut)
            {
                throw new TimeoutException($"Git timed out while staging '{relativePath}'.");
            }

            if (result.ExitCode != 0)
            {
                var details = string.IsNullOrWhiteSpace(result.StdErr)
                    ? "Git returned a non-zero exit code."
                    : result.StdErr;
                throw new InvalidOperationException($"Git could not stage '{relativePath}': {details}");
            }
        }

        public async Task<bool> IsStagingSafeAsync(string path, CancellationToken cancellationToken = default)
        {
            string rootPath;
            string relativePath;
            try
            {
                (rootPath, relativePath) = await ResolveRepositoryFileAsync(path, cancellationToken);
            }
            catch (InvalidOperationException)
            {
                // No repository, or the file lives outside it. Both are ordinary setups, and
                // both simply mean there is nothing to stage into.
                return false;
            }

            var result = await GitProcessRunner.RunAsync(
                ["diff", "--quiet", "--", relativePath],
                rootPath,
                StatusCommandTimeout,
                cancellationToken);

            // `git diff --quiet` exits 0 when the working tree matches the index and 1 when it
            // does not. Any other code is an error, and staging through it would be a guess.
            return !result.TimedOut && result.ExitCode == 0;
        }

        /// <summary>
        /// Resolves <paramref name="path"/> to (repository root, repository-relative path),
        /// rejecting anything that escapes the repository.
        /// </summary>
        private async Task<(string RootPath, string RelativePath)> ResolveRepositoryFileAsync(
            string path,
            CancellationToken cancellationToken)
        {
            var rootPath = await GetRootPathAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(rootPath))
            {
                throw new InvalidOperationException("Git could not locate the repository root.");
            }

            rootPath = Path.GetFullPath(rootPath);
            var fullPath = Path.GetFullPath(path, rootPath);
            var relativePath = Path.GetRelativePath(rootPath, fullPath);
            if (Path.IsPathRooted(relativePath)
                || relativePath.Equals("..", StringComparison.Ordinal)
                || relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Cannot stage a file outside the repository.");
            }

            return (rootPath, relativePath);
        }

        public async Task<string?> GetCurrentCommitHashAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                var hash = await RunGitCommandAsync("rev-parse HEAD", RootCommandTimeout, cancellationToken);
                return string.IsNullOrWhiteSpace(hash) ? null : hash.Trim();
            }
            catch
            {
                return null;
            }
        }

        public async Task<string?> GetCurrentBranchAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                var branch = await RunGitCommandAsync("rev-parse --abbrev-ref HEAD", RootCommandTimeout, cancellationToken);
                return string.IsNullOrWhiteSpace(branch) ? null : branch.Trim();
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
                    DiffCommandTimeout,
                    cancellationToken);

                // Also get status to detect new untracked files
                var statusOutput = await RunGitCommandAsync(
                    "status --porcelain=v1 --untracked-files=all",
                    StatusCommandTimeout,
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
                Log.Warning(ex, "[VibeRails] Error getting file changes");
            }

            return changes;
        }

        private async Task<string?> GetFileDiffAsync(string commitHash, string filePath, CancellationToken cancellationToken)
        {
            try
            {
                var diff = await RunGitCommandAsync(
                    $"diff {commitHash} -- \"{filePath}\"",
                    DiffCommandTimeout,
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

        private async Task<string> RunGitCommandAsync(
            string arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            var workingDirectory = _workingDirectory ?? Directory.GetCurrentDirectory();
            var result = await GitProcessRunner.RunAsync(
                $"--no-pager {arguments}",
                workingDirectory,
                timeout,
                cancellationToken);

            if (result.TimedOut)
            {
                Log.Warning(
                    "[Git] Timed out after {TimeoutSeconds}s: git {Arguments} (cwd: {WorkingDirectory})",
                    timeout.TotalSeconds,
                    arguments,
                    workingDirectory);
                return string.Empty;
            }

            return result.StdOut;
        }
    }
}
