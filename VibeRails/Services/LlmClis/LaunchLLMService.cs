using VibeRails.DTOs;
using VibeRails.Services.LlmClis.Launchers;

namespace VibeRails.Services.LlmClis
{
    public interface ILaunchLLMService
    {
        LaunchResult LaunchInTerminal(LLM llm, string? envName, string workingDirectory, string[] args);
        Dictionary<string, string> GetEnvironmentVariables(LLM llm, string envName);
        IBaseLlmCliLauncher GetLauncher(LLM llm);
    }

    public class LaunchLLMService : ILaunchLLMService
    {
        private readonly IClaudeLlmCliLauncher _claudeLauncher;
        private readonly ICodexLlmCliLauncher _codexLauncher;
        private readonly IGeminiLlmCliLauncher _geminiLauncher;
        private readonly ICopilotLlmCliLauncher _copilotLauncher;

        public LaunchLLMService(
            IClaudeLlmCliLauncher claudeLauncher,
            ICodexLlmCliLauncher codexLauncher,
            IGeminiLlmCliLauncher geminiLauncher,
            ICopilotLlmCliLauncher copilotLauncher)
        {
            _claudeLauncher = claudeLauncher;
            _codexLauncher = codexLauncher;
            _geminiLauncher = geminiLauncher;
            _copilotLauncher = copilotLauncher;
        }

        public IBaseLlmCliLauncher GetLauncher(LLM llm)
        {
            return llm switch
            {
                LLM.Claude => _claudeLauncher,
                LLM.Codex => _codexLauncher,
                LLM.Gemini => _geminiLauncher,
                LLM.Copilot => _copilotLauncher,
                _ => throw new ArgumentException($"Unsupported LLM type: {llm}")
            };
        }

        public LaunchResult LaunchInTerminal(
            LLM llm,
            string? envName,
            string workingDirectory,
            string[] args)
        {
            var launcher = GetLauncher(llm);
            return launcher.LaunchInTerminal(envName, workingDirectory, args);
        }

        public Dictionary<string, string> GetEnvironmentVariables(LLM llm, string envName)
        {
            var launcher = GetLauncher(llm);
            return launcher.GetEnvironmentVariables(envName);
        }
    }
}
