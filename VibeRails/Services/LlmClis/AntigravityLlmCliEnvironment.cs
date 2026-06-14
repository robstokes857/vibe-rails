using VibeRails.DTOs;
using VibeRails.Interfaces;

namespace VibeRails.Services.LlmClis
{
    // Launch-flag-only CLI (mirrors Copilot): no settings file, so no GetSettings/SaveSettings
    // and no /api/v1/.../settings route. All agy options (sandbox, YOLO, model, initial prompt)
    // are launch flags carried in CustomArgs / CustomPrompt.
    public class AntigravityLlmCliEnvironment : BaseLlmCliEnvironment, IAntigravityLlmCliEnvironment
    {
        public AntigravityLlmCliEnvironment(IFileService fileService) : base(fileService) { }

        public override string GetConfigSubdirectory() => "antigravity";

        public override async Task CreateEnvironment(LLM_Environment environment, CancellationToken cancellationToken)
        {
            var configPath = Path.Combine(environment.Path, GetConfigSubdirectory());
            EnsureDirectoryExists(configPath);
            await Task.CompletedTask;
        }
    }
}
