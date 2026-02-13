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
        private readonly IDbService _dbService;
        private readonly IFileService _fileService;

        public LlmCliEnvironmentService(
            IClaudeLlmCliEnvironment claudeLlmCliEnvironment,
            ICodexLlmCliEnvironment codexLlmCliEnvironment,
            IGeminiLlmCliEnvironment geminiLlmCliEnvironment,
            IDbService dbService,
            IFileService fileService)
        {
            _claudeLlmCliEnvironment = claudeLlmCliEnvironment;
            _codexLlmCliEnvironment = codexLlmCliEnvironment;
            _geminiLlmCliEnvironment = geminiLlmCliEnvironment;
            _dbService = dbService;
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
                default:
                    throw new ArgumentException("Unsupported LLM type");
            }
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
                    ["GEMINI_CONFIG_DIR"] = Path.Combine(envPath, "gemini")
                },
                _ => new Dictionary<string, string>()
            };
        }
    }
}
