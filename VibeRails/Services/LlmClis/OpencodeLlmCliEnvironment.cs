using VibeRails.DTOs;
using VibeRails.Interfaces;

namespace VibeRails.Services.LlmClis
{
    public class OpencodeLlmCliEnvironment : BaseLlmCliEnvironment, IOpencodeLlmCliEnvironment
    {
        public OpencodeLlmCliEnvironment(IFileService fileService) : base(fileService) { }

        public override string GetConfigSubdirectory() => "opencode";

        // Launch-flag-only (like Copilot/Antigravity): no settings file is written. VibeRails
        // points XDG_CONFIG_HOME at environment.Path, and OpenCode resolves this subdirectory as
        // its standard config/agents/commands/plugins root. XDG_DATA_HOME stays unchanged, so
        // credentials still live in the user's global OpenCode data directory.
        public override async Task CreateEnvironment(LLM_Environment environment, CancellationToken cancellationToken)
        {
            var configPath = Path.Combine(environment.Path, GetConfigSubdirectory());
            EnsureDirectoryExists(configPath);
            await Task.CompletedTask;
        }
    }
}
