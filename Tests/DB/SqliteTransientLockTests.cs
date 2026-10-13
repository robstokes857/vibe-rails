using Microsoft.Data.Sqlite;
using VibeRails.Data.Abstractions;
using VibeRails.Data.Sqlite;
using Xunit;

namespace Tests.DB;

/// <summary>
/// The shared retry for writes that must survive another process holding state.db's writer lock
/// past busy_timeout (VIBE-5): the job-run claim and the scheduler lease.
/// </summary>
public sealed class SqliteTransientLockTests
{
    [Theory]
    [InlineData(5)] // SQLITE_BUSY
    [InlineData(6)] // SQLITE_LOCKED
    public async Task RetryAsync_RunsTheOperationAgainAfterATransientLock(int errorCode)
    {
        var attempts = 0;

        var result = await SqliteTransientLock.RetryAsync(_ =>
        {
            attempts++;
            return attempts < 3
                ? throw new SqliteException($"SQLite Error {errorCode}", errorCode)
                : Task.FromResult("written");
        }, TestContext.Current.CancellationToken);

        Assert.Equal("written", result);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task RetryAsync_ReturnsAnAnswerAsIsEvenWhenItIsNo()
    {
        // A claim that changed 0 rows means another process won. That is an answer, not a lock.
        var attempts = 0;

        var claimed = await SqliteTransientLock.RetryAsync(_ =>
        {
            attempts++;
            return Task.FromResult(false);
        }, TestContext.Current.CancellationToken);

        Assert.False(claimed);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task RetryAsync_RethrowsTheLastLockAfterEveryRetry()
    {
        var attempts = 0;

        var thrown = await Assert.ThrowsAsync<SqliteException>(() => SqliteTransientLock.RetryAsync<bool>(_ =>
        {
            attempts++;
            throw new SqliteException($"locked {attempts}", 5);
        }, TestContext.Current.CancellationToken));

        Assert.Equal(SqliteTransientLock.RetryDelays.Count + 1, attempts);
        Assert.Equal($"locked {attempts}", thrown.Message);
    }

    [Fact]
    public async Task RetryAsync_DoesNotRetryAnythingElse()
    {
        var attempts = 0;

        await Assert.ThrowsAsync<SqliteException>(() => SqliteTransientLock.RetryAsync<bool>(_ =>
        {
            attempts++;
            throw new SqliteException("SQLite Error 19: constraint failed", 19);
        }, TestContext.Current.CancellationToken));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public void IsTransientLock_RecognizesATranslatedStorageException()
    {
        var inner = new SqliteException("SQLite Error 5", 5);
        Assert.True(SqliteTransientLock.IsTransientLock(new StorageException("busy", isTransient: true, inner)));
        Assert.False(SqliteTransientLock.IsTransientLock(new StorageException("broken", isTransient: false, inner)));
        Assert.False(SqliteTransientLock.IsTransientLock(new InvalidOperationException("no")));
    }
}
