using VibeRails.DTOs;
using VibeRails.Services.LlmClis.Launchers;

namespace VibeRails.Services.LlmClis
{
    public interface ILaunchLLMService
    {
        LaunchResult LaunchInTerminal(
            LLM llm,
            string? envName,
            string workingDirectory,
            string[] args,
            string[]? vbArgs = null,
            bool keepTerminalOpen = true,
            bool launchMinimized = false);
        Dictionary<string, string> GetEnvironmentVariables(LLM llm, string envName);
        IBaseLlmCliLauncher GetLauncher(LLM llm);
    }

    public class LaunchLLMService : ILaunchLLMService
    {
        private readonly IClaudeLlmCliLauncher _claudeLauncher;
        private readonly ICodexLlmCliLauncher _codexLauncher;
        private readonly IAntigravityLlmCliLauncher _antigravityLauncher;
        private readonly ICopilotLlmCliLauncher _copilotLauncher;
        private readonly IOpencodeLlmCliLauncher _opencodeLauncher;
        private readonly IGrokLlmCliLauncher _grokLauncher;

        public LaunchLLMService(
            IClaudeLlmCliLauncher claudeLauncher,
            ICodexLlmCliLauncher codexLauncher,
            IAntigravityLlmCliLauncher antigravityLauncher,
            ICopilotLlmCliLauncher copilotLauncher,
            IOpencodeLlmCliLauncher opencodeLauncher,
            IGrokLlmCliLauncher grokLauncher)
        {
            _claudeLauncher = claudeLauncher;
            _codexLauncher = codexLauncher;
            _antigravityLauncher = antigravityLauncher;
            _copilotLauncher = copilotLauncher;
            _opencodeLauncher = opencodeLauncher;
            _grokLauncher = grokLauncher;
        }

        public IBaseLlmCliLauncher GetLauncher(LLM llm)
        {
            return llm switch
            {
                LLM.Claude => _claudeLauncher,
                LLM.Codex => _codexLauncher,
                LLM.Antigravity => _antigravityLauncher,
                LLM.Copilot => _copilotLauncher,
                LLM.Grok46 => _grokLauncher,
                // Glm52 / Glm53 are OpenCode-backed pseudo-CLIs — they reuse the OpenCode
                // launcher (the --model pin is injected by CommandService.PrepareSession).
                LLM.OpenCode or LLM.Glm52 or LLM.Glm53 => _opencodeLauncher,
                _ => throw new ArgumentException($"Unsupported LLM type: {llm}")
            };
        }

        public LaunchResult LaunchInTerminal(
            LLM llm,
            string? envName,
            string workingDirectory,
            string[] args,
            string[]? vbArgs = null,
            bool keepTerminalOpen = true,
            bool launchMinimized = false)
        {
            var launcher = GetLauncher(llm);
            return launcher.LaunchInTerminal(
                llm,
                envName,
                workingDirectory,
                args,
                vbArgs,
                keepTerminalOpen,
                launchMinimized);
        }

        public Dictionary<string, string> GetEnvironmentVariables(LLM llm, string envName)
        {
            var launcher = GetLauncher(llm);
            return launcher.GetEnvironmentVariables(envName);
        }
    }
}
