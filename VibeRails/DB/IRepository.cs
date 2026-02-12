using VibeRails.DTOs;
using VibeRails.Services;

namespace VibeRails.DB
{
    public interface IRepository
    {
        // Environment operations (global, not project-scoped)
        Task<LLM_Environment?> GetEnvironmentByNameAndLlmAsync(string name, LLM llm, CancellationToken cancellationToken = default);
        Task<LLM_Environment?> FindEnvironmentByNameAsync(string name, CancellationToken cancellationToken = default);
        Task<LLM_Environment> GetOrCreateEnvironmentAsync(string name, LLM llm, CancellationToken cancellationToken = default);
        Task<List<LLM_Environment>> GetAllEnvironmentsAsync(CancellationToken cancellationToken = default);
        Task<List<LLM_Environment>> GetCustomEnvironmentsAsync(CancellationToken cancellationToken = default);
        Task<LLM_Environment> SaveEnvironmentAsync(LLM_Environment environment, CancellationToken cancellationToken = default);
        Task UpdateEnvironmentAsync(LLM_Environment environment, CancellationToken cancellationToken = default);
        Task DeleteEnvironmentAsync(int id, CancellationToken cancellationToken = default);

        // Sandbox operations (project-scoped)
        Task<Sandbox> SaveSandboxAsync(Sandbox sandbox, CancellationToken cancellationToken = default);
        Task<List<Sandbox>> GetSandboxesByProjectAsync(string projectPath, CancellationToken cancellationToken = default);
        Task<Sandbox?> GetSandboxByIdAsync(int id, CancellationToken cancellationToken = default);
        Task<Sandbox?> GetSandboxByNameAndProjectAsync(string name, string projectPath, CancellationToken cancellationToken = default);
        Task DeleteSandboxAsync(int id, CancellationToken cancellationToken = default);

        // Agent metadata operations
        Task<string?> GetAgentCustomNameAsync(string path, CancellationToken cancellationToken = default);
        Task SetAgentCustomNameAsync(string path, string customName, CancellationToken cancellationToken = default);

        // Project metadata operations
        Task<string?> GetProjectCustomNameAsync(string path, CancellationToken cancellationToken = default);
        Task SetProjectCustomNameAsync(string path, string customName, CancellationToken cancellationToken = default);
    }
}
