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
            // Set the environment path
            var envBasePath = ParserConfigs.GetEnvPath();
            environment.Path = Path.Combine(envBasePath, environment.CustomName);          
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
                    await _opencodeLlmCliEnvironment.SaveEnvironment(environment, cancellationToken);
                    break;
                // Glm52/KimiK3 are OpenCode-backed pseudo-CLIs — delegate to the OpenCode
                // environment so they share XDG_CONFIG_HOME isolation and config layout.
                case LLM.Glm52:
                case LLM.KimiK3:
                    await _opencodeLlmCliEnvironment.SaveEnvironment(environment, cancellationToken);
                    break;
                default:
                    throw new ArgumentException("Unsupported LLM type");
            }
        }

        public Task DeleteEnvironmentAsync(LLM_Environment environment, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(environment);

            var envBasePath = ParserConfigs.GetEnvPath();
            var environmentPath = string.IsNullOrWhiteSpace(environment.Path)
                ? Path.Combine(envBasePath, environment.CustomName)
                : environment.Path;

            // Containment guard: the create/launch paths are hardened, but environment.Path
            // here comes from a stored DB row that could predate that hardening or have been
            // hand-edited to point outside the envs root. Never recursively delete a path
            // outside the root — skip the filesystem delete and let the caller drop the DB row.
            if (!EnvironmentNameValidator.IsWithinEnvironmentRoot(envBasePath, environmentPath))
            {
                Log.Warning(
                    "[Environment] Refusing recursive delete of '{Path}' for environment '{Name}' — resolves outside the environments root.",
                    environmentPath, environment.CustomName);
                return Task.CompletedTask;
            }

            if (!_fileService.DirectoryExists(environmentPath))
            {
                return Task.CompletedTask;
            }

            _fileService.DeleteDirectory(environmentPath, recursive: true);
            return Task.CompletedTask;
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
                LLM.OpenCode => new Dictionary<string, string>
                {
                    ["XDG_CONFIG_HOME"] = envPath
                },
                // Glm52/KimiK3 are OpenCode-backed pseudo-CLIs — share the same XDG_CONFIG_HOME
                // isolation so per-env config/agents/commands/plugins stay separated.
                LLM.Glm52 or LLM.KimiK3 => new Dictionary<string, string>
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
