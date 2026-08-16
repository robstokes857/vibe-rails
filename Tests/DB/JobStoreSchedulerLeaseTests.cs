using Microsoft.Data.Sqlite;
using VibeRails.DB;
using Xunit;

namespace Tests.DB;

public sealed class JobStoreSchedulerLeaseTests : IDisposable
{
    private static readonly DateTime NowUtc =
        new(2026, 7, 26, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(30);

    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"viberails-job-scheduler-lease-{Guid.NewGuid():N}.db");
    private readonly string _connectionString;
    private readonly JobStore _first;
    private readonly JobStore _second;

    public JobStoreSchedulerLeaseTests()
    {
        _connectionString = $"Data Source={_dbPath};Mode=ReadWriteCreate;Cache=Shared";

        // Construct sequentially so this test is about lease contention, not concurrent schema
        // initialization. Each operation below still uses its own SQLite connection.
        _first = new JobStore(_connectionString);
        _second = new JobStore(_connectionString);
    }

    [Fact]
    public async Task TryAcquireOrRenewSchedulerLeaseAsync_AllowsExactlyOneConcurrentOwner()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        var attempts = await Task.WhenAll(
            _first.TryAcquireOrRenewSchedulerLeaseAsync(
                "instance-a", NowUtc, LeaseDuration, cancellationToken),
            _second.TryAcquireOrRenewSchedulerLeaseAsync(
                "instance-b", NowUtc, LeaseDuration, cancellationToken));

        Assert.Equal(1, attempts.Count(acquired => acquired));

        var winner = attempts[0] ? "instance-a" : "instance-b";
        var loser = attempts[0] ? "instance-b" : "instance-a";
        Assert.True(await _first.TryAcquireOrRenewSchedulerLeaseAsync(
            winner, NowUtc.AddSeconds(5), LeaseDuration, cancellationToken));
        Assert.False(await _second.TryAcquireOrRenewSchedulerLeaseAsync(
            loser, NowUtc.AddSeconds(5), LeaseDuration, cancellationToken));
    }

    [Fact]
    public async Task TryAcquireOrRenewSchedulerLeaseAsync_RenewalExtendsTheExpiry()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        Assert.True(await _first.TryAcquireOrRenewSchedulerLeaseAsync(
            "instance-a", NowUtc, LeaseDuration, cancellationToken));
        Assert.True(await _first.TryAcquireOrRenewSchedulerLeaseAsync(
            "instance-a", NowUtc.AddSeconds(20), LeaseDuration, cancellationToken));

        Assert.False(await _second.TryAcquireOrRenewSchedulerLeaseAsync(
            "instance-b", NowUtc.AddSeconds(31), LeaseDuration, cancellationToken));
        Assert.True(await _second.TryAcquireOrRenewSchedulerLeaseAsync(
            "instance-b", NowUtc.AddSeconds(50), LeaseDuration, cancellationToken));
    }

    [Fact]
    public async Task ReleaseSchedulerLeaseAsync_RequiresTheCurrentOwner()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        Assert.True(await _first.TryAcquireOrRenewSchedulerLeaseAsync(
            "instance-a", NowUtc, LeaseDuration, cancellationToken));
        Assert.True(await _second.TryAcquireOrRenewSchedulerLeaseAsync(
            "instance-b", NowUtc.Add(LeaseDuration), LeaseDuration, cancellationToken));

        Assert.False(await _first.ReleaseSchedulerLeaseAsync("instance-a", cancellationToken));
        Assert.False(await _first.TryAcquireOrRenewSchedulerLeaseAsync(
            "instance-a", NowUtc.AddSeconds(31), LeaseDuration, cancellationToken));
        Assert.True(await _second.ReleaseSchedulerLeaseAsync("instance-b", cancellationToken));
        Assert.True(await _first.TryAcquireOrRenewSchedulerLeaseAsync(
            "instance-a", NowUtc.AddSeconds(31), LeaseDuration, cancellationToken));
    }

    public void Dispose()
    {
        // Release only this class's pooled connections. A process-wide ClearAllPools() disposes
        // handles out from under DB test classes running in parallel (intermittent
        // ObjectDisposedException on SQLitePCL.sqlite3).
        SqliteConnection.ClearPool(new SqliteConnection(_connectionString));
        foreach (var path in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            try { File.Delete(path); }
            catch (IOException) { /* best-effort temp cleanup */ }
        }
    }
}
