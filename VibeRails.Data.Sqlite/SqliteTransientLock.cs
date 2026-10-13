using Microsoft.Data.Sqlite;
using VibeRails.Data.Abstractions;

namespace VibeRails.Data.Sqlite;

/// <summary>
/// SQLITE_BUSY (5) and SQLITE_LOCKED (6) reach the caller only after busy_timeout has already
/// waited 5 s: another process held the single writer lock on a file several VibeRails processes
/// share. Nothing was written, so the statement can simply run again. Retry only a write whose
/// failure would otherwise end something for good -- a run claim, the scheduler lease -- not every
/// statement.
/// </summary>
public static class SqliteTransientLock
{
    /// <summary>
    /// Pauses between attempts. Each attempt has its own busy_timeout wait, so four attempts ride
    /// out roughly 21 s of lock; the pauses only stop the retries from queueing back to back.
    /// </summary>
    public static IReadOnlyList<TimeSpan> RetryDelays { get; } =
    [
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(250),
        TimeSpan.FromMilliseconds(500),
    ];

    public static bool IsTransientLock(Exception exception) => exception switch
    {
        SqliteException sqlite => sqlite.SqliteErrorCode is 5 or 6,
        StorageException storage => storage.IsTransient,
        _ => false,
    };

    /// <summary>
    /// Runs <paramref name="operation"/>, running it again after a transient lock up to
    /// <see cref="RetryDelays"/> times. Only the exception is retried: whatever an attempt returns,
    /// including "0 rows changed", is the answer. The last lock exception is rethrown unchanged.
    /// </summary>
    public static async Task<T> RetryAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await operation(cancellationToken);
            }
            catch (Exception ex) when (attempt < RetryDelays.Count && IsTransientLock(ex))
            {
                await Task.Delay(RetryDelays[attempt], cancellationToken);
            }
        }
    }
}
