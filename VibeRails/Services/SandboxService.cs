using System.Text.RegularExpressions;
using Serilog;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Utils;

namespace VibeRails.Services
{
    /// <summary>
    /// How one sandbox differs from the default, user-created kind.
    /// </summary>
    /// <param name="CopyDirtyFiles">
    /// Whether the source project's dirty and untracked files are copied over the clone.
    /// True for a user-created sandbox: it is meant to continue the work in progress. False for
    /// an environment workspace in <see cref="DTOs.EnvironmentWorkspaceMode.PerRun"/> mode,
    /// where the entire point is a pristine tree — copying local mess in would defeat "fresh".
    /// Note the cost of false: no .env, no gitignored config. See the workspace docs.
    /// </param>
    /// <param name="EnvironmentId">
    /// The environment this sandbox is a workspace for, or null for a standalone sandbox.
    /// </param>
    public sealed record SandboxCreateOptions(
        bool CopyDirtyFiles = true,
        int? EnvironmentId = null);

    public interface ISandboxService
    {
        Task<Sandbox> CreateSandboxAsync(string name, string projectPath, SandboxCreateOptions? options = null, CancellationToken ct = default);
        Task DeleteSandboxAsync(int sandboxId, CancellationToken ct = default);
        Task<List<Sandbox>> GetSandboxesAsync(string projectPath, CancellationToken ct = default);
        Task<SandboxDiffResult> GetDiffAsync(int sandboxId, CancellationToken ct = default);
        Task<string> PushToRemoteAsync(int sandboxId, CancellationToken ct = default);
        Task<string> MergeLocallyAsync(int sandboxId, CancellationToken ct = default);
        /// <summary>
        /// Deletes a sandbox without letting failure propagate, returning whether it succeeded.
        /// Used when releasing an environment's workspaces: the directory is very often still
        /// locked by a CLI running inside it, and that must not fail the environment delete.
        /// </summary>
        Task<bool> TryDeleteSandboxAsync(int sandboxId, CancellationToken ct = default);
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
        private readonly string _sandboxBasePath;

        private static readonly Regex ValidNameRegex = new(@"^[a-zA-Z0-9_][a-zA-Z0-9_-]*$", RegexOptions.Compiled);
        private static readonly Regex ValidObjectIdRegex = new(
            @"^(?:[0-9a-fA-F]{40}|[0-9a-fA-F]{64})$",
            RegexOptions.Compiled);

        private static readonly TimeSpan GitCommandTimeout = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan GitCloneTimeout = TimeSpan.FromMinutes(10);

        // Bounds the name that becomes a path segment (Path.Combine) and a git branch.
        // Mirrors EnvironmentNameValidator's cap so an over-long name can't overrun MAX_PATH.
        private const int MaxSandboxNameLength = 64;
        private const long MaxDiffFileBytes = 5 * 1024 * 1024;

        public SandboxService(IRepository repository)
            : this(repository, GetConfiguredSandboxBasePath())
        {
        }

        internal SandboxService(IRepository repository, string sandboxBasePath)
        {
            _repository = repository;
            ArgumentException.ThrowIfNullOrWhiteSpace(sandboxBasePath);
            _sandboxBasePath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sandboxBasePath));
        }

        public async Task<Sandbox> CreateSandboxAsync(string name, string projectPath, SandboxCreateOptions? options = null, CancellationToken ct = default)
        {
            options ??= new SandboxCreateOptions();
            ValidateSandboxName(name);

            // Check for duplicate
            var existing = await _repository.GetSandboxByNameAndProjectAsync(name, projectPath, ct);
            if (existing != null)
                throw new InvalidOperationException($"A sandbox named '{name}' already exists for this project.");

            // Compute sandbox path (global sandboxes dir)
            var sandboxPath = ResolveSandboxChildPath(name);

            if (Directory.Exists(sandboxPath))
                throw new InvalidOperationException($"Directory already exists at '{sandboxPath}'. Choose a different name.");

            // Get current branch and commit hash from source project
            var gitService = new GitService(projectPath);
            var branch = await gitService.GetCurrentBranchAsync(ct) ?? "main";
            var commitHash = await gitService.GetCurrentCommitHashAsync(ct);

            // Shallow clone from local repo
            await RunGitCloneAsync(projectPath, sandboxPath, branch, ct);

            // Get dirty + untracked files from source and copy them over. Skipped for a
            // per-run workspace, whose contract is a tree matching the last commit exactly.
            if (options.CopyDirtyFiles)
                await CopyDirtyFilesAsync(projectPath, sandboxPath, ct);

            // If the source project has a real remote, point the sandbox at it
            // (the clone's origin currently points at the local filesystem path)
            var remoteUrl = await SetupSandboxRemoteAsync(projectPath, sandboxPath, ct);

            // Create and checkout a sandbox-specific branch
            var sandboxBranch = name;
            await RunGitCommandAsync(sandboxPath, ["checkout", "-b", sandboxBranch], throwOnError: true, ct);

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
                CreatedUTC = DateTime.UtcNow,
                EnvironmentId = options.EnvironmentId
            };

            return await _repository.SaveSandboxAsync(sandbox, ct);
        }

        public async Task<bool> TryDeleteSandboxAsync(int sandboxId, CancellationToken ct = default)
        {
            try
            {
                await DeleteSandboxAsync(sandboxId, ct);
                return true;
            }
            catch (Exception ex)
            {
                // Logged, not swallowed. The overwhelmingly common cause is a CLI still holding
                // files open inside the clone, and the caller's fallback (leaving the row as a
                // standalone sandbox) is what makes that recoverable — the user can delete it
                // from the Sandboxes card once the process exits.
                Log.Warning(
                    ex,
                    "[Sandbox] Could not delete sandbox {SandboxId}; it stays listed so it can be removed later",
                    sandboxId);
                return false;
            }
        }

        public async Task DeleteSandboxAsync(int sandboxId, CancellationToken ct = default)
        {
            var sandbox = await _repository.GetSandboxByIdAsync(sandboxId, ct);
            if (sandbox == null)
                throw new InvalidOperationException("Sandbox not found.");

            var sandboxPath = ResolveStoredSandboxPath(sandbox.Path);

            // Delete the directory if it exists
            if (Directory.Exists(sandboxPath))
            {
                EnsureSandboxDirectoryIsNotReparsePoint(sandboxPath);

                // Git marks objects as read-only; clear those flags so Directory.Delete works on Windows.
                // On Linux this is a harmless no-op for most files.
                ClearReadOnlyAttributes(sandboxPath);
                Directory.Delete(sandboxPath, recursive: true);
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

            var sandboxPath = ResolveStoredSandboxPath(sandbox.Path);
            if (!Directory.Exists(sandboxPath))
                throw new InvalidOperationException("Sandbox directory no longer exists.");
            EnsureSandboxDirectoryIsNotReparsePoint(sandboxPath);

            var baseCommit = sandbox.CommitHash?.Trim();
            if (!string.IsNullOrWhiteSpace(baseCommit) && !ValidObjectIdRegex.IsMatch(baseCommit))
                throw new InvalidOperationException("Sandbox base commit is invalid.");

            var files = new List<SandboxDiffFile>();

            // Get committed changes since the original commit
            var changedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrWhiteSpace(baseCommit))
            {
                var diffOutput = await RunGitCommandRawAsync(sandboxPath,
                    ["diff", "--name-only", "-z", $"{baseCommit}..HEAD", "--"], ct);
                if (!string.IsNullOrWhiteSpace(diffOutput))
                {
                    foreach (var path in diffOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries))
                        changedFiles.Add(path);
                }
            }

            // Also get uncommitted changes
            var statusOutput = await RunGitCommandRawAsync(sandboxPath,
                ["status", "--porcelain=v1", "-z", "--untracked-files=all", "--ignore-submodules"], ct);
            if (!string.IsNullOrWhiteSpace(statusOutput))
            {
                foreach (var (_, filePath) in ParsePorcelainStatus(statusOutput))
                    changedFiles.Add(filePath);
            }

            foreach (var filePath in changedFiles.OrderBy(f => f))
            {
                var fullPath = ResolveDiffFilePath(sandboxPath, filePath);

                // Get original content from the base commit
                var originalContent = "";
                if (!string.IsNullOrWhiteSpace(baseCommit))
                {
                    try
                    {
                        originalContent = await ReadOriginalContentAsync(
                            sandboxPath,
                            baseCommit,
                            filePath,
                            ct);
                    }
                    catch (InvalidOperationException)
                    {
                        // File didn't exist at the base commit (new file)
                    }
                }

                // Get modified content from disk
                var modifiedContent = "";
                if (TryGetReadableRegularFile(sandboxPath, fullPath, out var fileInfo)
                    && fileInfo.Length <= MaxDiffFileBytes
                    && await GitConfirmsRegularFileAsync(sandboxPath, fullPath, ct))
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

            ValidateStoredGitName(sandbox.Branch, "branch");
            var sandboxPath = ResolveStoredSandboxPath(sandbox.Path);
            if (!Directory.Exists(sandboxPath))
                throw new InvalidOperationException("Sandbox directory no longer exists.");
            EnsureSandboxDirectoryIsNotReparsePoint(sandboxPath);

            // Check for uncommitted changes
            var status = await RunGitCommandAsync(sandboxPath, ["status", "--porcelain"], ct);
            if (!string.IsNullOrWhiteSpace(status))
                throw new InvalidOperationException("Sandbox has uncommitted changes. Please commit or stash them before pushing.");

            await RunGitCommandAsync(
                sandboxPath,
                ["push", "-u", "origin", "--", sandbox.Branch],
                throwOnError: true,
                ct);

            return $"Branch '{sandbox.Branch}' pushed to remote successfully.";
        }

        public async Task<string> MergeLocallyAsync(int sandboxId, CancellationToken ct = default)
        {
            var sandbox = await _repository.GetSandboxByIdAsync(sandboxId, ct);
            if (sandbox == null)
                throw new InvalidOperationException("Sandbox not found.");

            ValidateStoredGitName(sandbox.Name, "name");
            ValidateStoredGitName(sandbox.Branch, "branch");
            var sandboxPath = ResolveStoredSandboxPath(sandbox.Path);
            if (!Directory.Exists(sandboxPath))
                throw new InvalidOperationException("Sandbox directory no longer exists.");
            EnsureSandboxDirectoryIsNotReparsePoint(sandboxPath);

            if (!Directory.Exists(sandbox.ProjectPath))
                throw new InvalidOperationException("Source project directory no longer exists.");

            // Auto-commit any uncommitted changes in the sandbox before merging
            var sandboxStatus = await RunGitCommandAsync(sandboxPath, ["status", "--porcelain"], ct);
            if (!string.IsNullOrWhiteSpace(sandboxStatus))
            {
                await RunGitCommandAsync(sandboxPath, ["add", "-A"], throwOnError: true, ct);
                await RunGitCommandAsync(
                    sandboxPath,
                    ["commit", "-m", "Auto-commit before merge"],
                    throwOnError: true,
                    ct);
            }

            // Stash any uncommitted changes in the source project so the merge can proceed
            var sourceStatus = await RunGitCommandAsync(sandbox.ProjectPath, ["status", "--porcelain"], ct);
            var sourceHasChanges = !string.IsNullOrWhiteSpace(sourceStatus);
            if (sourceHasChanges)
                await RunGitCommandAsync(
                    sandbox.ProjectPath,
                    ["stash", "push", "--include-untracked", "-m", "viberails-merge-stash"],
                    throwOnError: true,
                    ct);

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
                    ["remote", "add", "--", remoteName, sandboxPath], throwOnError: true, ct);

                await RunGitCommandAsync(sandbox.ProjectPath,
                    ["fetch", "--", remoteName], throwOnError: true, ct);

                await RunGitCommandAsync(sandbox.ProjectPath,
                    ["merge", "--no-edit", "--", $"{remoteName}/{sandbox.Branch}"], throwOnError: true, ct);

                return $"Sandbox '{sandbox.Name}' merged into '{sourceBranch}' successfully.";
            }
            catch (Exception ex) when (ex.Message.Contains("CONFLICT", StringComparison.OrdinalIgnoreCase) ||
                                       ex.Message.Contains("merge", StringComparison.OrdinalIgnoreCase))
            {
                try { await RunGitCommandAsync(sandbox.ProjectPath, ["merge", "--abort"], ct); }
                catch { /* best effort abort */ }

                throw new InvalidOperationException(
                    "Merge conflict detected. The merge has been aborted. " +
                    "You may need to manually merge the changes.");
            }
            finally
            {
                try { await RunGitCommandAsync(sandbox.ProjectPath, ["remote", "remove", "--", remoteName], ct); }
                catch { /* best effort cleanup */ }

                if (sourceHasChanges)
                {
                    try { await RunGitCommandAsync(sandbox.ProjectPath, ["stash", "pop"], ct); }
                    catch { /* best effort stash restore */ }
                }
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
                remoteUrl = await RunGitCommandAsync(projectPath, ["remote", "get-url", "origin"], ct);
            }
            catch
            {
                // No remote configured in source — that's fine
            }

            if (!string.IsNullOrWhiteSpace(remoteUrl))
            {
                // Source has a real remote — point the sandbox at it
                await RunGitCommandAsync(
                    sandboxPath,
                    ["remote", "set-url", "--", "origin", remoteUrl],
                    throwOnError: true,
                    ct);
                return remoteUrl;
            }
            else
            {
                // Source has no remote — remove the local-path origin from the sandbox
                try
                {
                    await RunGitCommandAsync(sandboxPath, ["remote", "remove", "--", "origin"], ct);
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
            var workingDirectory = Path.GetDirectoryName(destPath)
                ?? throw new InvalidOperationException("Sandbox destination has no parent directory.");
            var result = await GitProcessRunner.RunAsync(
                ["clone", "--depth", "1", "--branch", branch, "--single-branch", "--", sourcePath, destPath],
                workingDirectory,
                GitCloneTimeout,
                ct);

            if (result.TimedOut)
                throw new TimeoutException("Git clone timed out.");
            if (result.ExitCode != 0)
                throw new InvalidOperationException($"Git clone failed: {result.StdErr}");
        }

        private static async Task CopyDirtyFilesAsync(string projectPath, string sandboxPath, CancellationToken ct)
        {
            // Get all dirty/untracked files via git status --porcelain
            var output = await RunGitCommandRawAsync(
                projectPath,
                ["status", "--porcelain=v1", "-z", "--untracked-files=all", "--ignore-submodules"],
                ct);

            if (string.IsNullOrWhiteSpace(output))
                return;

            foreach (var (statusCode, filePath) in ParsePorcelainStatus(output))
            {
                // Skip .vibe_rails directory contents
                if (filePath.StartsWith($"{PathConstants.DEFAULT_INSTALL_DIR_NAME}/", StringComparison.OrdinalIgnoreCase))
                    continue;

                var sourceFull = ResolveDiffFilePath(projectPath, filePath);
                var destFull = ResolveDiffFilePath(sandboxPath, filePath);

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
                if (TryGetReadableRegularFile(projectPath, sourceFull, out _)
                    && await GitConfirmsRegularFileAsync(projectPath, sourceFull, ct))
                {
                    var destDir = Path.GetDirectoryName(destFull);
                    if (destDir != null)
                    {
                        EnsureDirectoryPathIsSafe(sandboxPath, destDir);
                        Directory.CreateDirectory(destDir);
                        EnsureDirectoryPathIsSafe(sandboxPath, destDir);
                    }

                    EnsureDestinationFileIsSafe(destFull);
                    File.Copy(sourceFull, destFull, overwrite: true);
                }
            }
        }

        private static void ClearReadOnlyAttributes(string directory)
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                IgnoreInaccessible = false,
                ReturnSpecialDirectories = false
            };

            foreach (var file in Directory.EnumerateFiles(directory, "*", options))
            {
                var attrs = File.GetAttributes(file);
                if ((attrs & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(file, attrs & ~FileAttributes.ReadOnly);
            }
        }

        private static IEnumerable<(string StatusCode, string FilePath)> ParsePorcelainStatus(string output)
        {
            var entries = output.Split('\0');
            for (var index = 0; index < entries.Length; index++)
            {
                var entry = entries[index];
                if (entry.Length < 4)
                    continue;

                var statusCode = entry[..2];
                var filePath = entry[3..];
                yield return (statusCode, filePath);

                // With porcelain v1 -z, a rename/copy is "XY target\0source\0".
                // The target is the path whose working-tree content matters; skip the source.
                if ((statusCode.Contains('R') || statusCode.Contains('C'))
                    && index + 1 < entries.Length)
                {
                    index++;
                }
            }
        }

        private static string GetConfiguredSandboxBasePath()
        {
            var configuredPath = ParserConfigs.GetSandboxPath();
            return string.IsNullOrWhiteSpace(configuredPath)
                ? Path.Combine(PathConstants.GetInstallDirPath(), PathConstants.SANDBOXES_SUBDIR)
                : configuredPath;
        }

        private static void ValidateSandboxName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new InvalidOperationException("Sandbox name is required.");
            if (name.Length > MaxSandboxNameLength)
                throw new InvalidOperationException($"Sandbox name must be {MaxSandboxNameLength} characters or fewer.");
            if (!ValidNameRegex.IsMatch(name))
            {
                throw new InvalidOperationException(
                    "Sandbox name must start with an alphanumeric character or underscore and can only contain alphanumeric characters, hyphens, and underscores.");
            }
        }

        private static void ValidateStoredGitName(string value, string field)
        {
            if (string.IsNullOrWhiteSpace(value)
                || value.Length > MaxSandboxNameLength
                || !ValidNameRegex.IsMatch(value))
            {
                throw new InvalidOperationException($"Sandbox {field} is invalid.");
            }
        }

        private string ResolveSandboxChildPath(string name) =>
            ResolveStoredSandboxPath(Path.Combine(_sandboxBasePath, name));

        private string ResolveStoredSandboxPath(string storedPath)
        {
            if (string.IsNullOrWhiteSpace(storedPath))
                throw new InvalidOperationException("Sandbox path is invalid.");

            string fullPath;
            try
            {
                fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(storedPath));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                throw new InvalidOperationException("Sandbox path is invalid.", ex);
            }

            if (!IsStrictChildPath(_sandboxBasePath, fullPath))
            {
                throw new InvalidOperationException(
                    "Refusing to access a sandbox outside the configured sandbox directory.");
            }

            return fullPath;
        }

        internal static string ResolveDiffFilePath(string sandboxPath, string relativePath)
        {
            if (string.IsNullOrEmpty(relativePath) || Path.IsPathRooted(relativePath))
                throw new InvalidOperationException("Git returned an invalid sandbox file path.");

            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sandboxPath));
            string fullPath;
            try
            {
                var platformPath = relativePath.Replace('/', Path.DirectorySeparatorChar);
                fullPath = Path.GetFullPath(Path.Combine(root, platformPath));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                throw new InvalidOperationException("Git returned an invalid sandbox file path.", ex);
            }

            if (!IsStrictChildPath(root, fullPath))
            {
                throw new InvalidOperationException(
                    "Refusing to read a file outside the sandbox directory.");
            }

            return fullPath;
        }

        private void EnsureSandboxDirectoryIsNotReparsePoint(string sandboxPath)
        {
            EnsureDirectoryPathIsSafe(_sandboxBasePath, sandboxPath);
        }

        private static void EnsureDirectoryPathIsSafe(string rootPath, string directoryPath)
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
            var current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directoryPath));
            if (!PathsEqual(root, current) && !IsStrictChildPath(root, current))
                throw new InvalidOperationException("Directory path escapes its configured root.");

            while (!PathsEqual(root, current))
            {
                var info = new DirectoryInfo(current);
                if (info.Exists
                    && (info.LinkTarget is not null
                        || info.Attributes.HasFlag(FileAttributes.ReparsePoint)))
                {
                    throw new InvalidOperationException(
                        "Refusing to access a sandbox through a symbolic link or junction.");
                }

                current = Path.GetDirectoryName(current)
                    ?? throw new InvalidOperationException("Directory path escapes its configured root.");
            }
        }

        private static bool TryGetReadableRegularFile(
            string rootPath,
            string fullPath,
            out FileInfo fileInfo)
        {
            fileInfo = new FileInfo(fullPath);
            try
            {
                if (!fileInfo.Exists
                    || fileInfo.LinkTarget is not null
                    || fileInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    return false;
                }

                EnsureDirectoryPathIsSafe(
                    rootPath,
                    fileInfo.DirectoryName
                        ?? throw new InvalidOperationException("Sandbox file has no parent directory."));
                _ = fileInfo.Length;
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                return false;
            }
        }

        internal static async Task<bool> GitConfirmsRegularFileAsync(
            string rootPath,
            string fullPath,
            CancellationToken ct)
        {
            var relativePath = Path.GetRelativePath(rootPath, fullPath).Replace('\\', '/');
            var result = await GitProcessRunner.RunAsync(
                ["--no-pager", "hash-object", "--no-filters", "--", relativePath],
                rootPath,
                GitCommandTimeout,
                ct);

            return !result.TimedOut && result.ExitCode == 0;
        }

        private static void EnsureDestinationFileIsSafe(string path)
        {
            var info = new FileInfo(path);
            try
            {
                if (Directory.Exists(path)
                    || info.LinkTarget is not null
                    || (info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint)))
                {
                    throw new InvalidOperationException(
                        "Refusing to overwrite a symbolic link, junction, or directory in the sandbox.");
                }
            }
            catch (FileNotFoundException)
            {
                // A missing destination is safe to create.
            }
            catch (DirectoryNotFoundException)
            {
                // The caller creates and validates the parent directory before copying.
            }
        }

        private static async Task<string> ReadOriginalContentAsync(
            string sandboxPath,
            string commitHash,
            string filePath,
            CancellationToken ct)
        {
            var objectName = $"{commitHash}:{filePath}";
            var sizeOutput = await RunGitCommandAsync(
                sandboxPath,
                ["cat-file", "-s", objectName],
                throwOnError: true,
                ct);
            if (!long.TryParse(sizeOutput, out var size) || size < 0)
                throw new InvalidOperationException("Git returned an invalid file size.");
            if (size > MaxDiffFileBytes)
                return string.Empty;

            return await RunGitCommandAsync(
                sandboxPath,
                ["show", objectName],
                throwOnError: true,
                ct);
        }

        private static bool IsStrictChildPath(string rootPath, string candidatePath)
        {
            var rootWithSeparator = Path.TrimEndingDirectorySeparator(rootPath) + Path.DirectorySeparatorChar;
            return candidatePath.StartsWith(rootWithSeparator, PathComparison);
        }

        private static bool PathsEqual(string left, string right) =>
            string.Equals(left, right, PathComparison);

        private static StringComparison PathComparison =>
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        private static Task<string> RunGitCommandAsync(
            string workingDirectory,
            IReadOnlyList<string> arguments,
            CancellationToken ct) =>
            RunGitCommandAsync(workingDirectory, arguments, throwOnError: false, ct);

        private static Task<string> RunGitCommandRawAsync(
            string workingDirectory,
            IReadOnlyList<string> arguments,
            CancellationToken ct) =>
            RunGitCommandCoreAsync(
                workingDirectory,
                arguments,
                throwOnError: false,
                preserveOutput: true,
                ct);

        private static Task<string> RunGitCommandAsync(
            string workingDirectory,
            IReadOnlyList<string> arguments,
            bool throwOnError,
            CancellationToken ct) =>
            RunGitCommandCoreAsync(
                workingDirectory,
                arguments,
                throwOnError,
                preserveOutput: false,
                ct);

        private static async Task<string> RunGitCommandCoreAsync(
            string workingDirectory,
            IReadOnlyList<string> arguments,
            bool throwOnError,
            bool preserveOutput,
            CancellationToken ct)
        {
            var gitArguments = new List<string>(arguments.Count + 1) { "--no-pager" };
            gitArguments.AddRange(arguments);
            var result = preserveOutput
                ? await GitProcessRunner.RunRawAsync(
                    gitArguments,
                    workingDirectory,
                    GitCommandTimeout,
                    ct)
                : await GitProcessRunner.RunAsync(
                    gitArguments,
                    workingDirectory,
                    GitCommandTimeout,
                    ct);

            if (result.TimedOut)
                throw new TimeoutException("Git command timed out.");

            if (throwOnError && result.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    !string.IsNullOrWhiteSpace(result.StdErr)
                        ? result.StdErr
                        : $"Git command failed with exit code {result.ExitCode}");
            }

            return result.StdOut;
        }
    }
}
