using VibeRails.Data.Sqlite;
using VibeRails.Services.Launches;
using Xunit;

namespace Tests.Services.Launches;

/// <summary>VIBE-321: a held lease is renewed after half its duration, not on every scheduler tick.</summary>
public sealed class HeldLeasesTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "viberails-held-leases-" + Guid.NewGuid().ToString("N"));
    private readonly LaunchStore store;
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public HeldLeasesTests()
    {
        Directory.CreateDirectory(root);
        store = new LaunchStore($"Data Source={Path.Combine(root, "launches.db")};Pooling=False");
    }

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task RenewsOnlyAfterHalfTheDurationAndStaysExclusive()
    {
        var leases = new HeldLeases(TimeSpan.FromSeconds(30));
        var writes = 0;
        Task<bool> Renew(DateTime at, TimeSpan duration) { writes++; return store.TryLeaseAsync("scheduler", "a", at, duration, Ct); }
        var now = new DateTime(2026, 11, 13, 6, 0, 0, DateTimeKind.Utc);

        for (var second = 0; second < 15; second += 2)
            Assert.True(await leases.HoldAsync("scheduler", now.AddSeconds(second), Renew));
        Assert.Equal(1, writes);
        // Another root still cannot take the lease that was written once at t=0.
        Assert.False(await store.TryLeaseAsync("scheduler", "b", now.AddSeconds(14), TimeSpan.FromSeconds(30), Ct));

        Assert.True(await leases.HoldAsync("scheduler", now.AddSeconds(16), Renew));
        Assert.Equal(2, writes);
        // The renewal at t=16 keeps the lease beyond the first write's expiry.
        Assert.False(await store.TryLeaseAsync("scheduler", "b", now.AddSeconds(40), TimeSpan.FromSeconds(30), Ct));
        Assert.True(await store.TryLeaseAsync("scheduler", "b", now.AddSeconds(46), TimeSpan.FromSeconds(30), Ct));
    }

    [Fact]
    public async Task ALeaseHeldElsewhereIsRetriedOnEveryTickAndThenTaken()
    {
        var now = new DateTime(2026, 11, 13, 6, 0, 0, DateTimeKind.Utc);
        Assert.True(await store.TryLeaseAsync("scheduler", "other root", now, TimeSpan.FromSeconds(30), Ct));
        var leases = new HeldLeases(TimeSpan.FromSeconds(30));
        var writes = 0;
        Task<bool> Renew(DateTime at, TimeSpan duration) { writes++; return store.TryLeaseAsync("scheduler", "a", at, duration, Ct); }

        Assert.False(await leases.HoldAsync("scheduler", now.AddSeconds(2), Renew));
        Assert.False(await leases.HoldAsync("scheduler", now.AddSeconds(4), Renew));
        Assert.Equal(2, writes);
        Assert.True(await leases.HoldAsync("scheduler", now.AddSeconds(30), Renew));
        Assert.True(await leases.HoldAsync("scheduler", now.AddSeconds(32), Renew));
        Assert.Equal(3, writes);
    }

    [Fact]
    public async Task AFailedRenewalOrABackwardClockForcesAWrite()
    {
        var leases = new HeldLeases(TimeSpan.FromSeconds(30));
        var now = new DateTime(2026, 11, 13, 6, 0, 0, DateTimeKind.Utc);
        var result = true;
        var writes = 0;
        Task<bool> Renew(DateTime at, TimeSpan duration) { writes++; return Task.FromResult(result); }

        Assert.True(await leases.HoldAsync("project:X", now, Renew));
        // The clock moved back a minute: the remembered expiry cannot be trusted.
        Assert.True(await leases.HoldAsync("project:X", now.AddMinutes(-1), Renew));
        Assert.Equal(2, writes);
        result = false;
        Assert.False(await leases.HoldAsync("project:X", now.AddMinutes(-1).AddSeconds(20), Renew));
        // Lost: no longer remembered, so the next tick writes again instead of assuming it is held.
        Assert.False(await leases.HoldAsync("project:X", now.AddMinutes(-1).AddSeconds(22), Renew));
        Assert.Equal(4, writes);
        // Names are independent.
        result = true;
        Assert.True(await leases.HoldAsync("scheduler", now, Renew));
        Assert.Equal(5, writes);
    }
}
