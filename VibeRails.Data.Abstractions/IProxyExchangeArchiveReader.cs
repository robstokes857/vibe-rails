namespace VibeRails.Data.Abstractions;

/// <summary>Prepares optional, exactly attributed proxy data independently of ordinary session backup.</summary>
public interface IProxyExchangeArchiveReader
{
    Task<PreparedProxyArchive> PrepareAsync(string sessionId, CancellationToken cancellationToken);
}

/// <summary>An owned, complete JSON array staged on disk, or evidence that coverage is empty/unavailable.</summary>
public sealed class PreparedProxyArchive(
    string status, long? count = null, DateTime? snapshotUtc = null, Stream? content = null) : IAsyncDisposable
{
    public string Status { get; } = status;
    public long? Count { get; } = count;
    public DateTime? SnapshotUtc { get; } = snapshotUtc;
    public Stream? Content { get; } = content;
    public ValueTask DisposeAsync() => Content?.DisposeAsync() ?? ValueTask.CompletedTask;
}
