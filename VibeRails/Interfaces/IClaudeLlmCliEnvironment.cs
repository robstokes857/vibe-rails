using VibeRails.DTOs;

namespace VibeRails.Interfaces
{
    public interface IClaudeLlmCliEnvironment : IBaseLlmCliEnvironment
    {
        Task<ClaudeSettingsDto> GetSettings(string envName, CancellationToken cancellationToken);
        Task SaveSettings(string envName, ClaudeSettingsDto settings, CancellationToken cancellationToken);
    }
}
