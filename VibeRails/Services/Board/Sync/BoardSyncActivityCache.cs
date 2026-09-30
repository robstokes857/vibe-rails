using Microsoft.Extensions.Caching.Memory;

namespace VibeRails.Services.Board.Sync;

/// <summary>
/// Process-local successful activity checks shared by scoped sync services. Only small hashes
/// and times are cached, never recordings, code or credentials. Destination/board/card identities
/// partition entries; the durable cursor and acknowledgements remain authoritative.
/// </summary>
public sealed class BoardSyncActivityCache(TimeProvider? timeProvider = null) : IDisposable
{
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(1);
    private readonly MemoryCache entries = new(new MemoryCacheOptions { SizeLimit = 10000 });
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    internal sealed record Entry(string Hash, DateTimeOffset CheckedUtc, DateTimeOffset UploadedUtc);
    internal DateTimeOffset UtcNow => clock.GetUtcNow();
    internal Entry? Get(string key) => entries.TryGetValue(key, out Entry? entry) ? entry : null;
    internal void Set(string key, Entry entry) => entries.Set(key, entry,
        new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(2) });
    public void Dispose() => entries.Dispose();
}
