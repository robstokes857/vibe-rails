using VibeRails.Jobs;
using VibeRails.Services;
using Xunit;

namespace Tests.Jobs;

public sealed class ProxyRetentionScheduleTests
{
    private static readonly DateTime Now = new(2026, 9, 29, 20, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task FirstCheckIsDueAndADeferralHoldsAcrossProcessesUntilItExpires()
    {
        var cache = new FakeGlobalCache();
        var schedule = new ProxyRetentionSchedule();
        Assert.True(await schedule.IsDueAsync(cache, Now));

        await schedule.DeferAsync(cache, Now);
        Assert.False(await schedule.IsDueAsync(cache, Now.AddHours(7)));
        // Another root backend, a fresh instance, reads the persisted deferral.
        Assert.False(await new ProxyRetentionSchedule().IsDueAsync(cache, Now.AddHours(7)));
        var expiry = Now + ProxyRetentionSchedule.RecheckInterval;
        Assert.True(await schedule.IsDueAsync(cache, expiry));
        Assert.True(await new ProxyRetentionSchedule().IsDueAsync(cache, expiry));
    }

    [Fact]
    public async Task InMemoryCopyAnswersWithoutTheCacheUntilItExpires()
    {
        var cache = new FakeGlobalCache();
        var schedule = new ProxyRetentionSchedule();
        await schedule.DeferAsync(cache, Now);
        cache.Reads = 0;
        Assert.False(await schedule.IsDueAsync(cache, Now.AddHours(1)));
        Assert.Equal(0, cache.Reads);
        Assert.True(await schedule.IsDueAsync(cache, Now.AddHours(9)));
        Assert.Equal(1, cache.Reads);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not a date")]
    // Beyond the cap: a wrong clock when the value was written must not silence retention for years.
    [InlineData("2030-01-01T00:00:00.0000000Z")]
    public async Task UnusableOrImplausibleDeferralsAreIgnored(string? stored)
    {
        var cache = new FakeGlobalCache();
        if (stored is not null)
            await cache.SetAsync(ProxyRetentionSchedule.CacheKey, stored);
        Assert.True(await new ProxyRetentionSchedule().IsDueAsync(cache, Now));
    }

    private sealed class FakeGlobalCache : IGlobalCache
    {
        private readonly Dictionary<string, string> _values = new();
        public int Reads;

        public Task<string?> GetAsync(string key)
        {
            Reads++;
            return Task.FromResult(_values.TryGetValue(key, out var value) ? value : null);
        }

        public Task SetAsync(string key, string value)
        {
            _values[key] = value;
            return Task.CompletedTask;
        }

        public Task<bool> GetAsBoolAsync(string key, bool defaultValue = false) => throw new NotSupportedException();
        public Task<int?> GetAsIntAsync(string key) => throw new NotSupportedException();
        public Task<Dictionary<string, string>> GetAllAsync() => Task.FromResult(new Dictionary<string, string>(_values));

        public Task RemoveAsync(string key)
        {
            _values.Remove(key);
            return Task.CompletedTask;
        }
    }
}
