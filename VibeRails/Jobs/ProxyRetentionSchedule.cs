using System.Globalization;
using VibeRails.Services;

namespace VibeRails.Jobs;

/// <summary>
/// Decides whether a retention tick should scan proxy exchanges at all. A completed pass that
/// found nothing means nothing becomes eligible until an acknowledged exchange ages past the
/// retention window, so the next check is deferred: remembered in this process for its lifetime
/// and persisted in the global cache so every root backend, and a restarted one, honours the same
/// deferral. <see cref="RecheckInterval"/> caps the delay for a session acknowledged late whose
/// exchanges are already past the window; a deferral value beyond that cap is treated as garbage
/// so a wrong clock cannot silence retention.
/// </summary>
public sealed class ProxyRetentionSchedule
{
    public const string CacheKey = "retention.proxy.nextCheckUtc";
    public static readonly TimeSpan RecheckInterval = TimeSpan.FromHours(8);
    private DateTime? _dueUtc;

    /// <summary>True when the proxy pass should run now.</summary>
    public async Task<bool> IsDueAsync(IGlobalCache cache, DateTime nowUtc)
    {
        if (_dueUtc is { } due)
        {
            if (nowUtc < due && due <= nowUtc + RecheckInterval)
                return false;
            _dueUtc = null;
        }
        if (DateTime.TryParseExact(await cache.GetAsync(CacheKey), "O", CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var persisted)
            && nowUtc < persisted && persisted <= nowUtc + RecheckInterval)
        {
            _dueUtc = persisted;
            return false;
        }
        return true;
    }

    /// <summary>Records that a completed pass found nothing, so the next check waits.</summary>
    public async Task DeferAsync(IGlobalCache cache, DateTime nowUtc)
    {
        var due = nowUtc + RecheckInterval;
        await cache.SetAsync(CacheKey, due.ToString("O", CultureInfo.InvariantCulture));
        _dueUtc = due;
    }
}
