using Serilog;
using VibeRails.DTOs;
using VibeRails.Interfaces;
using VibeRails.Utils;

namespace VibeRails.Services.LlmClis
{
    public class LlmCliEnvironmentService
    {
        private readonly IClaudeLlmCliEnvironment _claudeLlmCliEnvironment;
        private readonly ICodexLlmCliEnvironment _codexLlmCliEnvironment;
        private readonly IAntigravityLlmCliEnvironment _antigravityLlmCliEnvironment;
        private readonly ICopilotLlmCliEnvironment _copilotLlmCliEnvironment;
        private readonly IOpencodeLlmCliEnvironment _opencodeLlmCliEnvironment;
        private readonly IFileService _fileService;

        public LlmCliEnvironmentService(
            IClaudeLlmCliEnvironment claudeLlmCliEnvironment,
            ICodexLlmCliEnvironment codexLlmCliEnvironment,
            IAntigravityLlmCliEnvironment antigravityLlmCliEnvironment,
            ICopilotLlmCliEnvironment copilotLlmCliEnvironment,
            IOpencodeLlmCliEnvironment opencodeLlmCliEnvironment,
            IFileService fileService)
        {
            _claudeLlmCliEnvironment = claudeLlmCliEnvironment;
            _codexLlmCliEnvironment = codexLlmCliEnvironment;
            _antigravityLlmCliEnvironment = antigravityLlmCliEnvironment;
            _copilotLlmCliEnvironment = copilotLlmCliEnvironment;
            _opencodeLlmCliEnvironment = opencodeLlmCliEnvironment;
            _fileService = fileService;
        }

        public async Task CreateEnvironmentAsync(LLM_Environment environment, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(environment);

            // Treat the service boundary as a security boundary too. Routes validate names, but
            // direct/future callers must not be able to redirect copied CLI credentials outside
            // the configured environments root.
            var envBasePath = ParserConfigs.GetEnvPath();
            environment.Path = EnvironmentNameValidator.ResolveEnvironmentDirectory(
                envBasePath,
                environment.CustomName);
            environment.LastUsedUTC = DateTime.UtcNow;

            switch (environment.LLM)
            {
                case LLM.Codex:
                    await _codexLlmCliEnvironment.SaveEnvironment(environment, cancellationToken);
                    break;
                case LLM.Claude:
                    await _claudeLlmCliEnvironment.SaveEnvironment(environment, cancellationToken);
                    break;
                case LLM.Antigravity:
                    await _antigravityLlmCliEnvironment.SaveEnvironment(environment, cancellationToken);
                    break;
                case LLM.Copilot:
                    await _copilotLlmCliEnvironment.SaveEnvironment(environment, cancellationToken);
                    break;
                case LLM.OpenCode:
                // Glm52 / Grok46 / Glm53 are OpenCode-backed pseudo-CLIs — delegate to the OpenCode
                // environment so they share XDG_CONFIG_HOME isolation and config layout.
                case LLM.Glm52:
                case LLM.Grok46:
                case LLM.Glm53:
                    await _opencodeLlmCliEnvironment.SaveEnvironment(environment, cancellationToken);
                    break;
                default:
                    throw new ArgumentException("Unsupported LLM type");
            }
        }

        /// <summary>
        /// Atomically moves an environment directory to a unique sibling before its database row
        /// becomes eligible for deletion. Keeping the original path absent while the row still
        /// exists prevents a concurrent create from claiming that path until the guarded database
        /// delete has completed.
        /// </summary>
        public StagedEnvironmentDirectory StageEnvironmentDirectoryForDeletion(LLM_Environment environment)
        {
            ArgumentNullException.ThrowIfNull(environment);

            var envBasePath = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(ParserConfigs.GetEnvPath()));
            var environmentPath = string.IsNullOrWhiteSpace(environment.Path)
                ? EnvironmentNameValidator.ResolveEnvironmentDirectory(envBasePath, environment.CustomName)
                : Path.TrimEndingDirectorySeparator(Path.GetFullPath(environment.Path));

            // environment.Path comes from a stored DB row that could predate create-time
            // hardening or have been hand-edited. Never move or recursively delete it when it
            // resolves outside the configured root; the caller may still remove the stale row.
            if (!EnvironmentNameValidator.IsWithinEnvironmentRoot(envBasePath, environmentPath))
            {
                Log.Warning(
                    "[Environment] Refusing filesystem deletion of '{Path}' for environment '{Name}' — resolves outside the environments root.",
                    environmentPath, environment.CustomName);
                return new StagedEnvironmentDirectory(envBasePath, originalPath: null, tombstonePath: null);
            }

            var parentPath = Path.GetDirectoryName(environmentPath)
                ?? throw new InvalidOperationException("Environment directory has no parent.");

            if (!_fileService.DirectoryExists(environmentPath))
            {
                // A second delete request must not interpret the first request's atomic rename as
                // a genuinely absent config directory and delete the row out from under it.
                if (HasDeletionTombstone(parentPath, environment.Id))
                {
                    throw new EnvironmentDeletionInProgressException(environment.Id);
                }

                return new StagedEnvironmentDirectory(envBasePath, environmentPath, tombstonePath: null);
            }

            // A same-parent rename is atomic on the supported filesystems and cannot cross a
            // volume boundary. The random name prevents one delete request from reusing another
            // request's tombstone.
            var tombstonePath = Path.Combine(
                parentPath,
                $".viberails-delete-{environment.Id}-{Guid.NewGuid():N}");

            if (!EnvironmentNameValidator.IsWithinEnvironmentRoot(envBasePath, tombstonePath))
            {
                throw new InvalidOperationException("Environment deletion tombstone resolved outside the environments root.");
            }

            try
            {
                _fileService.MoveDirectory(environmentPath, tombstonePath);
            }
            catch (IOException) when (
                !_fileService.DirectoryExists(environmentPath)
                && HasDeletionTombstone(parentPath, environment.Id))
            {
                // Two requests may both observe the original before either rename. The loser of
                // the atomic move reports a conflict and, critically, never reaches the DB delete.
                throw new EnvironmentDeletionInProgressException(environment.Id);
            }

            return new StagedEnvironmentDirectory(envBasePath, environmentPath, tombstonePath);
        }

        private bool HasDeletionTombstone(string parentPath, int environmentId)
        {
            if (!_fileService.DirectoryExists(parentPath))
            {
                return false;
            }

            var prefix = $".viberails-delete-{environmentId}-";
            var directories = _fileService.EnumerateDirectories(
                parentPath,
                $"{prefix}*",
                new EnumerationOptions { RecurseSubdirectories = false });

            return directories?.Any(path =>
                Path.GetFileName(path).StartsWith(prefix, StringComparison.Ordinal)) == true;
        }

        /// <summary>
        /// Restores a staged directory when the guarded database delete was refused or failed.
        /// Existing content at the original path is never overwritten.
        /// </summary>
        public void RestoreStagedEnvironmentDirectory(StagedEnvironmentDirectory stagedDirectory)
        {
            ArgumentNullException.ThrowIfNull(stagedDirectory);
            ValidateStagedDirectory(stagedDirectory);

            if (stagedDirectory.TombstonePath == null
                || !_fileService.DirectoryExists(stagedDirectory.TombstonePath))
            {
                return;
            }

            if (stagedDirectory.OriginalPath == null)
            {
                throw new InvalidOperationException("A staged environment tombstone has no original path.");
            }

            if (_fileService.DirectoryExists(stagedDirectory.OriginalPath))
            {
                throw new IOException(
                    $"Refusing to overwrite an environment directory recreated at '{stagedDirectory.OriginalPath}'.");
            }

            _fileService.MoveDirectory(stagedDirectory.TombstonePath, stagedDirectory.OriginalPath);
        }

        /// <summary>
        /// Finalizes a successful database deletion by deleting only the staged tombstone. A new
        /// environment created at the original path after the row deletion is left untouched.
        /// </summary>
        public void FinalizeStagedEnvironmentDirectoryDeletion(StagedEnvironmentDirectory stagedDirectory)
        {
            ArgumentNullException.ThrowIfNull(stagedDirectory);
            ValidateStagedDirectory(stagedDirectory);

            if (stagedDirectory.TombstonePath != null
                && _fileService.DirectoryExists(stagedDirectory.TombstonePath))
            {
                _fileService.DeleteDirectory(stagedDirectory.TombstonePath, recursive: true);
            }
        }

        private static void ValidateStagedDirectory(StagedEnvironmentDirectory stagedDirectory)
        {
            if (stagedDirectory.OriginalPath != null
                && !EnvironmentNameValidator.IsWithinEnvironmentRoot(
                    stagedDirectory.EnvironmentRoot,
                    stagedDirectory.OriginalPath))
            {
                throw new InvalidOperationException("Staged environment path resolves outside the environments root.");
            }

            if (stagedDirectory.TombstonePath != null
                && !EnvironmentNameValidator.IsWithinEnvironmentRoot(
                    stagedDirectory.EnvironmentRoot,
                    stagedDirectory.TombstonePath))
            {
                throw new InvalidOperationException("Staged environment tombstone resolves outside the environments root.");
            }
        }

        public sealed class StagedEnvironmentDirectory
        {
            internal StagedEnvironmentDirectory(
                string environmentRoot,
                string? originalPath,
                string? tombstonePath)
            {
                EnvironmentRoot = environmentRoot;
                OriginalPath = originalPath;
                TombstonePath = tombstonePath;
            }

            internal string EnvironmentRoot { get; }
            public string? OriginalPath { get; }
            public string? TombstonePath { get; }
        }

        public sealed class EnvironmentDeletionInProgressException : InvalidOperationException
        {
            internal EnvironmentDeletionInProgressException(int environmentId)
                : base($"Environment {environmentId} is already being deleted.")
            {
            }
        }

        public Dictionary<string, string> GetEnvironmentVariables(string envName, LLM llm)
        {
            var envBasePath = ParserConfigs.GetEnvPath();
            // envName arrives unvalidated from the launch routes (terminal/start, cli/launch,
            // sandbox) — EnvironmentNameValidator.Validate only runs on create. Resolve through
            // the containment guard so a "../" / absolute name can't point these env vars
            // outside the envs root.
            var envPath = EnvironmentNameValidator.ResolveEnvironmentDirectory(envBasePath, envName);

            return llm switch
            {
                LLM.Claude => new Dictionary<string, string>
                {
                    ["CLAUDE_CONFIG_DIR"] = Path.Combine(envPath, "claude")
                },
                LLM.Codex => new Dictionary<string, string>
                {
                    ["CODEX_HOME"] = Path.Combine(envPath, "codex")
                },
                // Antigravity (agy) is launch-flag-only: no verified per-environment config-dir
                // env var, so (like Copilot) we inject none.
                LLM.Antigravity => new Dictionary<string, string>(),
                LLM.Copilot => new Dictionary<string, string>(),
                // OPENCODE_CONFIG_DIR is an additive overlay and still loads the user's global
                // config. Point OpenCode's XDG config root at the environment root instead;
                // OpenCode then resolves its config under the existing "opencode" subdirectory.
                // XDG_DATA_HOME stays untouched so credentials remain shared.
                LLM.OpenCode or LLM.Glm52 or LLM.Grok46 or LLM.Glm53 => new Dictionary<string, string>
                {
                    ["XDG_CONFIG_HOME"] = envPath
                },
                _ => new Dictionary<string, string>()
            };
        }

        /// <summary>
        /// Per-LLM environment variables independent of any custom environment (they apply
        /// even when no envName is set). Central home for LLM-specific env injection so
        /// one-off "if (llm == X)" tweaks stay out of call sites like CommandService.
        /// </summary>
        public Dictionary<string, string> GetBaseEnvironmentVariables(LLM llm)
        {
            return llm switch
            {
                // Force DEC 2026 sync output regardless of TERM. Claude Code 2.1.110+ gates
                // BSU/ESU on a hardcoded TERM allowlist (xterm-ghostty/kitty) our ConPTY child
                // doesn't land on, so without it the post-resize redraws aren't bracketed and
                // xterm.js commits the intermediate frame. See runbooks/terminal/TERMINAL.md
                // resize-reprint entry + anthropics/claude-code#49584, #55613.
                LLM.Claude => new Dictionary<string, string> { ["CLAUDE_CODE_FORCE_SYNC_OUTPUT"] = "1" },
                _ => new Dictionary<string, string>()
            };
        }
    }
}
