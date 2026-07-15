using Microsoft.Data.Sqlite;
using Serilog;

namespace VibeRails.DB;

/// <summary>
/// Running totals over three windows: all time (all days/providers), the current UTC month, and
/// this server process ("session"). Bytes are measured; tokens are a display heuristic.
/// </summary>
public sealed record TokenSavingsTotals(
    long BytesBefore,
    long BytesAfter,
    long SessionBytesBefore = 0,
    long SessionBytesAfter = 0,
    long MonthBytesBefore = 0,
    long MonthBytesAfter = 0)
{
    public long BytesSaved => BytesBefore - BytesAfter;

    /// <summary>
    /// ~4 bytes/token heuristic, derived only at read time (never persisted) so history reprices
    /// for free if a real tokenizer ever replaces the estimate.
    /// </summary>
    public long TokensSaved => BytesSaved / 4;

    public long SessionTokensSaved => (SessionBytesBefore - SessionBytesAfter) / 4;

    public long MonthTokensSaved => (MonthBytesBefore - MonthBytesAfter) / 4;
}

/// <summary>
/// Persists the token-saver tally to state.db (one row per UTC day per provider) and serves the
/// running total the UI displays.
/// </summary>
public interface ITokenSavingsStore
{
    /// <summary>
    /// Records one rewritten request. Never blocks and never throws: the in-memory total updates
    /// synchronously; the DB upsert runs fire-and-forget with failures swallowed (deliberate —
    /// savings telemetry is never worth a slower or riskier relay; worst case an increment is lost).
    /// </summary>
    void Record(string provider, int bytesBefore, int bytesAfter);

    /// <summary>
    /// Current running totals (persisted history + this session's records), broken out into
    /// all-time, current-UTC-month, and this-process windows.
    /// </summary>
    TokenSavingsTotals GetTotals();
}

/// <summary>
/// The single place where token-saver persistence errors are allowed to be eaten. Everything else
/// about the write path is deliberately boring: connection-per-write like <see cref="Repository"/>,
/// plus <c>busy_timeout</c> (which <see cref="Repository"/> famously lacks) because this writer
/// races background embedding jobs for the same database file.
/// </summary>
public sealed class TokenSavingsStore(string connectionString) : ITokenSavingsStore
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private long _bytesBefore;
    private long _bytesAfter;
    private long _sessionBytesBefore;
    private long _sessionBytesAfter;
    private volatile bool _loaded;

    // Current-UTC-month tally. Guarded by its own lock (not Interlocked) because a month rollover
    // must swap the key and zero both counters as one unit.
    private readonly object _monthLock = new();
    private string _monthKey = "";
    private long _monthBytesBefore;
    private long _monthBytesAfter;

    /// <summary>Last fire-and-forget persist, observable so tests can await the write settling.</summary>
    internal Task LastPersist { get; private set; } = Task.CompletedTask;

    /// <summary>Testable clock; month-rollover behavior is unreachable under the real one.</summary>
    internal Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

    public void Record(string provider, int bytesBefore, int bytesAfter)
    {
        Interlocked.Add(ref _bytesBefore, bytesBefore);
        Interlocked.Add(ref _bytesAfter, bytesAfter);
        Interlocked.Add(ref _sessionBytesBefore, bytesBefore);
        Interlocked.Add(ref _sessionBytesAfter, bytesAfter);
        AddToMonth(CurrentMonthKey(), bytesBefore, bytesAfter);
        LastPersist = PersistAsync(provider, bytesBefore, bytesAfter);
    }

    public TokenSavingsTotals GetTotals()
    {
        if (!_loaded)
            _ = PersistAsync(null, 0, 0); // kick the lazy history load; this call returns what's in memory

        var (monthBefore, monthAfter) = ReadMonth(CurrentMonthKey());
        return new TokenSavingsTotals(
            Interlocked.Read(ref _bytesBefore),
            Interlocked.Read(ref _bytesAfter),
            Interlocked.Read(ref _sessionBytesBefore),
            Interlocked.Read(ref _sessionBytesAfter),
            monthBefore,
            monthAfter);
    }

    private string CurrentMonthKey() => UtcNow().ToString("yyyy-MM");

    private void AddToMonth(string monthKey, long bytesBefore, long bytesAfter)
    {
        lock (_monthLock)
        {
            // Keys sort chronologically ("yyyy-MM"), so an older key is a stale late add (e.g. the
            // lazy history load landing just after a rollover) and must not resurrect last month.
            if (string.CompareOrdinal(monthKey, _monthKey) < 0)
                return;

            if (_monthKey != monthKey)
            {
                _monthKey = monthKey;
                _monthBytesBefore = 0;
                _monthBytesAfter = 0;
            }

            _monthBytesBefore += bytesBefore;
            _monthBytesAfter += bytesAfter;
        }
    }

    private (long Before, long After) ReadMonth(string monthKey)
    {
        lock (_monthLock)
        {
            // A stale key means nothing was recorded since the month rolled over: report zero
            // rather than last month's counters.
            return _monthKey == monthKey ? (_monthBytesBefore, _monthBytesAfter) : (0L, 0L);
        }
    }

    private async Task PersistAsync(string? provider, int bytesBefore, int bytesAfter)
    {
        // Hop off the caller's thread FIRST: WaitAsync completes synchronously when the semaphore
        // is free (the common case), and everything below is synchronous SQLite — without this
        // yield, "fire-and-forget" would actually run the whole DB round-trip (up to a 5s
        // busy-wait under state.db contention) inline on the relay hot path before the SSE copy.
        await Task.Yield();

        // Serializing writes keeps concurrent relays from ever contending inside SQLite, and makes
        // the lazy load ordering sound: history is summed before this session's first row lands, so
        // nothing is ever counted twice.
        await _writeLock.WaitAsync();
        try
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            using (var pragma = connection.CreateCommand())
            {
                pragma.CommandText = "PRAGMA busy_timeout = 5000;";
                pragma.ExecuteNonQuery();
            }

            using (var create = connection.CreateCommand())
            {
                // The store can run before the first Repository initializes the schema.
                create.CommandText = SqlStrings.CreateTokenSavingsTable;
                create.ExecuteNonQuery();
            }

            if (!_loaded)
            {
                using (var totals = connection.CreateCommand())
                {
                    totals.CommandText = SqlStrings.SelectTokenSavingsTotals;
                    using var reader = totals.ExecuteReader();
                    if (reader.Read())
                    {
                        Interlocked.Add(ref _bytesBefore, reader.GetInt64(0));
                        Interlocked.Add(ref _bytesAfter, reader.GetInt64(1));
                    }
                }

                // Session rows recorded before this load already counted into the month tally at
                // Record() time and are still queued behind this semaphore, so the history sum
                // cannot double-count them (same ordering argument as the all-time load above).
                var monthKey = CurrentMonthKey();
                using (var month = connection.CreateCommand())
                {
                    month.CommandText = SqlStrings.SelectTokenSavingsMonthTotals;
                    month.Parameters.AddWithValue("$month", monthKey);
                    using var reader = month.ExecuteReader();
                    if (reader.Read())
                        AddToMonth(monthKey, reader.GetInt64(0), reader.GetInt64(1));
                }

                _loaded = true;
            }

            if (provider is null)
                return;

            using var upsert = connection.CreateCommand();
            upsert.CommandText = SqlStrings.UpsertTokenSavings;
            upsert.Parameters.AddWithValue("$day", UtcNow().ToString("yyyy-MM-dd"));
            upsert.Parameters.AddWithValue("$provider", provider);
            // Requests counts every measured request; RewrittenRequests only those that shrank —
            // the gap is the canary for an upstream tool rename (savings flatline, traffic doesn't).
            upsert.Parameters.AddWithValue("$rewritten", bytesBefore != bytesAfter ? 1 : 0);
            upsert.Parameters.AddWithValue("$bytesBefore", bytesBefore);
            upsert.Parameters.AddWithValue("$bytesAfter", bytesAfter);
            upsert.Parameters.AddWithValue("$updatedUTC", UtcNow().ToString("o"));
            upsert.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            // Deliberate swallow (user decision): a lost tally increment is acceptable, a relay
            // slowed or failed by telemetry is not. This catch gates nothing but the tally itself.
            Log.Debug(ex, "Token-savings tally write failed; increment dropped.");
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
