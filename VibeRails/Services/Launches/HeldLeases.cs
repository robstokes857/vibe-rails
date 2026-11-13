namespace VibeRails.Services.Launches;

/// <summary>
/// Remembers the runtime leases one scheduler holds, so a 2 s tick renews each lease only after half
/// its duration instead of committing a write to launches.db on every tick (VIBE-321). A lease that
/// another root holds, or one that fails to renew, is forgotten and attempted again on the next tick.
/// </summary>
internal sealed class HeldLeases(TimeSpan duration)
{
    private readonly Dictionary<string, DateTime> until = new(StringComparer.Ordinal);

    /// <summary>The lease duration written on each renewal.</summary>
    public TimeSpan Duration => duration;

    /// <summary>
    /// True while the named lease is held. Calls <paramref name="renew"/> only when the lease is not
    /// known to be held, has passed half its duration, or the clock moved backwards since it was taken.
    /// </summary>
    public async Task<bool> HoldAsync(string name, DateTime nowUtc, Func<DateTime, TimeSpan, Task<bool>> renew)
    {
        if (until.TryGetValue(name, out var expires) && nowUtc >= expires - duration && nowUtc < expires - duration / 2)
            return true;
        if (await renew(nowUtc, duration))
        {
            until[name] = nowUtc + duration;
            return true;
        }
        until.Remove(name);
        return false;
    }
}
