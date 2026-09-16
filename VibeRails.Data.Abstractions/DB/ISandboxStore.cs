using VibeRails.DTOs;

namespace VibeRails.DB;

public interface ISandboxStore
{
    Task<Sandbox> SaveSandboxAsync(Sandbox sandbox, CancellationToken cancellationToken = default);

    Task<List<Sandbox>> GetSandboxesByProjectAsync(string projectPath, CancellationToken cancellationToken = default);

    Task<Sandbox?> GetSandboxByIdAsync(int id, CancellationToken cancellationToken = default);

    Task<Sandbox?> GetSandboxByNameAndProjectAsync(string name, string projectPath, CancellationToken cancellationToken = default);

    /// <summary>Workspaces owned by one environment, newest first.</summary>
    Task<List<Sandbox>> GetSandboxesByEnvironmentIdAsync(int environmentId, CancellationToken cancellationToken = default);

    /// <summary>Releases an environment's workspaces back to standalone sandboxes.</summary>
    Task OrphanSandboxesForEnvironmentAsync(int environmentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// True when a session is still open in <paramref name="directory"/> or below it.
    /// Guards workspace retention: a clone with a live run in it must never be pruned.
    /// </summary>
    Task<bool> HasOpenSessionUnderDirectoryAsync(string directory, CancellationToken cancellationToken = default);

    Task DeleteSandboxAsync(int id, CancellationToken cancellationToken = default);
}
