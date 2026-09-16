using VibeRails.DTOs;

namespace VibeRails.DB;

public interface IMetadataStore
{
    Task<string> GetProjectDisplayNameAsync(string path, CancellationToken cancellationToken = default);

    Task<bool> UpdateLatestProjectDisplayNameAsync(string path, string projectDisplayName, CancellationToken cancellationToken = default);

    Task<string?> GetAgentCustomNameAsync(string path, CancellationToken cancellationToken = default);

    Task SetAgentCustomNameAsync(string path, string customName, CancellationToken cancellationToken = default);

    Task<string?> GetProjectCacheValueAsync(string projectPath, string key, CancellationToken cancellationToken = default);

    Task SetProjectCacheValueAsync(string projectPath, string key, string value, CancellationToken cancellationToken = default);

    Task<Dictionary<string, string>> GetAllProjectCacheAsync(string projectPath, CancellationToken cancellationToken = default);

    Task RemoveProjectCacheValueAsync(string projectPath, string key, CancellationToken cancellationToken = default);

    Task<string?> GetGlobalCacheValueAsync(string key, CancellationToken cancellationToken = default);

    Task SetGlobalCacheValueAsync(string key, string value, CancellationToken cancellationToken = default);

    Task<Dictionary<string, string>> GetAllGlobalCacheAsync(CancellationToken cancellationToken = default);

    Task RemoveGlobalCacheValueAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically saves or removes the global LLM-picker document and applies
    /// Environment visibility changes in the same storage transaction.
    /// </summary>
    Task SaveLlmPickerStateAsync(
        string cacheKey,
        string? preferenceJson,
        IReadOnlyDictionary<int, bool> environmentHidden,
        CancellationToken cancellationToken = default);
}
