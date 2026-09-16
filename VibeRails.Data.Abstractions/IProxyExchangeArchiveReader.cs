namespace VibeRails.Data.Abstractions;

/// <summary>Prepares optional, exactly attributed proxy data independently of ordinary session backup.</summary>
public interface IProxyExchangeArchiveReader
{
    Task<PreparedProxyArchive> PrepareAsync(string sessionId, CancellationToken cancellationToken);
}

/// <summary>An owned, complete JSON array staged on disk, or evidence that coverage is empty/unavailable.</summary>
/// <param name="maxRowId">
/// The largest proxy rowid this snapshot contains, or null when it contains no rows. Retention
/// uses it as the boundary of what was backed up: the proxy queues an exchange with its CreatedUTC
/// and writes it later, possibly after the snapshot was taken, so neither CreatedUTC nor the
/// session's acknowledgement time can say whether a given row was inside the acknowledged envelope.
/// </param>
public sealed class PreparedProxyArchive(
    string status, long? count = null, DateTime? snapshotUtc = null, Stream? content = null, long? maxRowId = null) : IAsyncDisposable
{
    public string Status { get; } = status;
    public long? Count { get; } = count;
    public DateTime? SnapshotUtc { get; } = snapshotUtc;
    public Stream? Content { get; } = content;
    public long? MaxRowId { get; } = maxRowId;
    public ValueTask DisposeAsync() => Content?.DisposeAsync() ?? ValueTask.CompletedTask;
}
