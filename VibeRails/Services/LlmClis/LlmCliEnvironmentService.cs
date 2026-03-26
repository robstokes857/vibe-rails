using VibeRails.DTOs;
using VibeRails.Interfaces;
using VibeRails.Utils;

namespace VibeRails.Services.LlmClis
{
    public class LlmCliEnvironmentService
    {
        private readonly IClaudeLlmCliEnvironment _claudeLlmCliEnvironment;
        private readonly ICodexLlmCliEnvironment _codexLlmCliEnvironment;
        private readonly IGeminiLlmCliEnvironment _geminiLlmCliEnvironment;
        private readonly ICopilotLlmCliEnvironment _copilotLlmCliEnvironment;
        private readonly IFileService _fileService;

        public LlmCliEnvironmentService(
            IClaudeLlmCliEnvironment claudeLlmCliEnvironment,
            ICodexLlmCliEnvironment codexLlmCliEnvironment,
            IGeminiLlmCliEnvironment geminiLlmCliEnvironment,
            ICopilotLlmCliEnvironment copilotLlmCliEnvironment,
            IFileService fileService)
        {
            _claudeLlmCliEnvironment = claudeLlmCliEnvironment;
            _codexLlmCliEnvironment = codexLlmCliEnvironment;
            _geminiLlmCliEnvironment = geminiLlmCliEnvironment;
            _copilotLlmCliEnvironment = copilotLlmCliEnvironment;
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
                case LLM.Gemini:
                    await _geminiLlmCliEnvironment.SaveEnvironment(environment, cancellationToken);
                    break;
                case LLM.Copilot:
                    await _copilotLlmCliEnvironment.SaveEnvironment(environment, cancellationToken);
                    break;
                default:
                    throw new ArgumentException("Unsupported LLM type");
            }
        }

        public Task DeleteEnvironmentAsync(LLM_Environment environment, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(environment);

            var environmentPath = string.IsNullOrWhiteSpace(environment.Path)
                ? Path.Combine(ParserConfigs.GetEnvPath(), environment.CustomName)
                : environment.Path;

            if (string.IsNullOrWhiteSpace(environmentPath) || !_fileService.DirectoryExists(environmentPath))
            {
                return Task.CompletedTask;
            }

            _fileService.DeleteDirectory(environmentPath, recursive: true);
            return Task.CompletedTask;
        }


        public Dictionary<string, string> GetEnvironmentVariables(string envName, LLM llm)
        {
            var envBasePath = ParserConfigs.GetEnvPath();
            var envPath = Path.Combine(envBasePath, envName);

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
                LLM.Gemini => new Dictionary<string, string>
                {
                    ["XDG_CONFIG_HOME"] = Path.Combine(envPath, "gemini", "config"),
                    ["XDG_DATA_HOME"] = Path.Combine(envPath, "gemini", "data"),
                    ["XDG_CACHE_HOME"] = Path.Combine(envPath, "gemini", "cache"),
                    ["XDG_STATE_HOME"] = Path.Combine(envPath, "gemini", "state")
                },
                LLM.Copilot => new Dictionary<string, string>(),
                _ => new Dictionary<string, string>()
            };
        }
    }
}
