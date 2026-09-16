namespace VibeRails.Data.Abstractions;

/// <summary>Applies local age limits to ended sessions acknowledged by the backup server.</summary>
public interface IDataRetentionStore
{
    Task<DataRetentionResult> PruneAsync(DateTime nowUtc, CancellationToken cancellationToken);
}

/// <param name="EmbeddingRowsDeleted">
/// Vector-store rows removed for pruned sessions. Retention that clears state.db but leaves these
/// behind is not a cleanup: BertDocumentResponseMapper falls back to the vector document's own
/// stored Text when the state metadata is gone, so the pruned conversation stays searchable.
/// </param>
public sealed record DataRetentionResult(
    int SessionsDeleted,
    long StateRowsDeleted,
    long ProxyExchangesDeleted,
    long EmbeddingRowsDeleted = 0);
