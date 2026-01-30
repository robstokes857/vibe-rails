using VibeRails.DTOs;

namespace VibeRails.Interfaces
{
    public interface IBaseLlmCliEnvironment
    {
        Task CreateEnvironment(LLM_Environment environment, CancellationToken cancellationToken);
        Task SaveEnvironment(LLM_Environment environment, CancellationToken cancellationToken);
        string GetConfigSubdirectory();
    }
}
