using VibeRails.DTOs;
using VibeRails.Interfaces;

namespace VibeRails.Services.LlmClis
{
    public class CopilotLlmCliEnvironment : BaseLlmCliEnvironment, ICopilotLlmCliEnvironment
    {
        public CopilotLlmCliEnvironment(IDbService dbService, IFileService fileService) : base(dbService, fileService) { }

        public override string GetConfigSubdirectory() => "copilot";

        public override async Task CreateEnvironment(LLM_Environment environment, CancellationToken cancellationToken)
        {
            var configPath = Path.Combine(environment.Path, GetConfigSubdirectory());
            EnsureDirectoryExists(configPath);
            await Task.CompletedTask;
        }
    }
}
