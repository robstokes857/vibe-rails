using VibeRails.DTOs;
using VibeRails.Interfaces;

namespace VibeRails.Services.LlmClis
{
    // Launch-flag-only CLI (mirrors Copilot/Antigravity): no settings file, so no
    // GetSettings/SaveSettings and no /api/v1/.../settings route. A grok/ env
    // subdirectory can exist for future settings but must never become $GROK_HOME —
    // that would isolate auth.json the way setting OpenCode's XDG_DATA_HOME would.
    public class GrokLlmCliEnvironment : BaseLlmCliEnvironment, IGrokLlmCliEnvironment
    {
        public GrokLlmCliEnvironment(IFileService fileService) : base(fileService) { }

        public override string GetConfigSubdirectory() => "grok";

        public override async Task CreateEnvironment(LLM_Environment environment, CancellationToken cancellationToken)
        {
            var configPath = Path.Combine(environment.Path, GetConfigSubdirectory());
            EnsureDirectoryExists(configPath);
            await Task.CompletedTask;
        }
    }
}
