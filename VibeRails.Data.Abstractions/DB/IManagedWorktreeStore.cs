namespace VibeRails.DB;

/// <summary>A worktree created by an Initial Message, with an optional absolute expiry.</summary>
public sealed record ManagedWorktree(
    string Id, string Path, string GitCommonDirectory, string GitDirectory, string Branch,
    DateTime CreatedUTC, DateTime? DeleteAfterUTC);

/// <summary>Durable ownership and retention for prompt-created Git worktrees in state.db.</summary>
public interface IManagedWorktreeStore
{
    /// <summary>Records ownership and the original retention deadline for a new worktree.</summary>
    Task AddAsync(ManagedWorktree worktree, CancellationToken ct = default);
    /// <summary>Lists worktrees whose ownership records have not been removed.</summary>
    Task<IReadOnlyList<ManagedWorktree>> ListAsync(CancellationToken ct = default);
    /// <summary>Lists directories still referenced by unfinished sessions, including older clients.</summary>
    Task<IReadOnlyList<string>> GetOpenSessionDirectoriesAsync(CancellationToken ct = default);
    /// <summary>Forgets a worktree after verified filesystem cleanup.</summary>
    Task RemoveAsync(string id, CancellationToken ct = default);
}
