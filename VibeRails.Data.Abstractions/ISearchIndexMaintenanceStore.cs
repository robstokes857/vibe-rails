namespace VibeRails.Data.Abstractions;

public interface ISearchIndexMaintenanceStore
{
    /// <summary>Completes a bounded batch of queued search-index writes; failed batches remain retryable.</summary>
    Task<int> RepairPendingAsync(int maxRows = 100, CancellationToken cancellationToken = default);
}
