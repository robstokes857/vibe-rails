using VibeRails.DTOs;

namespace VibeRails.Interfaces
{
    public interface ICodexLlmCliEnvironment : IBaseLlmCliEnvironment
    {
        Task<CodexSettingsDto> GetSettings(string envName, CancellationToken cancellationToken);
        Task SaveSettings(string envName, CodexSettingsDto settings, CancellationToken cancellationToken);
    }
}
