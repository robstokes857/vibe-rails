namespace VibeRails.Data.Abstractions;

public interface ISearchIndexMaintenanceStore
{
    /// <summary>Completes a bounded batch of queued search-index writes; failed batches remain retryable.</summary>
    Task<int> RepairPendingAsync(int maxRows = 100, CancellationToken cancellationToken = default);

    /// <summary>
    /// Rebuilds the derived search index from its content table when the two have drifted apart
    /// (for example after an older binary wrote to the index directly). Returns true when a rebuild
    /// happened. Does nothing while queued writes are still pending.
    /// </summary>
    Task<bool> RepairIndexIfInconsistentAsync(CancellationToken cancellationToken = default);
}
