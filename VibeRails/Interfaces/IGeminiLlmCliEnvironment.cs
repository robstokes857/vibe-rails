using VibeRails.DTOs;

namespace VibeRails.Interfaces
{
    public interface IGeminiLlmCliEnvironment : IBaseLlmCliEnvironment
    {
        Task<GeminiSettingsDto> GetSettings(string envName, CancellationToken cancellationToken);
        Task SaveSettings(string envName, GeminiSettingsDto settings, CancellationToken cancellationToken);
    }
}
