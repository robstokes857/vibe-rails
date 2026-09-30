namespace VibeRails.Data.Abstractions;

/// <summary>Applies local age limits to ended sessions acknowledged by the backup server.</summary>
public interface IDataRetentionStore
{
    /// <summary>Prunes state, vector and proxy data in one pass.</summary>
    Task<DataRetentionResult> PruneAsync(DateTime nowUtc, CancellationToken cancellationToken);

    /// <param name="includeProxy">
    /// false skips the proxy-exchange pass. The caller owns that schedule: a completed pass that
    /// found nothing can be deferred, because nothing becomes eligible until an acknowledged
    /// exchange ages past the retention window.
    /// </param>
    Task<DataRetentionResult> PruneAsync(DateTime nowUtc, bool includeProxy, CancellationToken cancellationToken);
}

/// <param name="EmbeddingRowsDeleted">
/// Vector-store rows removed for pruned sessions. Retention that clears state.db but leaves these
/// behind is not a cleanup: BertDocumentResponseMapper falls back to the vector document's own
/// stored Text when the state metadata is gone, so the pruned conversation stays searchable.
/// </param>
/// <param name="ProxyPruneComplete">
/// True when the proxy pass ran and nothing eligible remains. False when it was skipped or its
/// batch cap stopped it early, so the caller must run it again next tick rather than defer it.
/// </param>
public sealed record DataRetentionResult(
    int SessionsDeleted,
    long StateRowsDeleted,
    long ProxyExchangesDeleted,
    long EmbeddingRowsDeleted = 0,
    bool ProxyPruneComplete = false);
