using Microsoft.Data.Sqlite;
using Serilog;

namespace VibeRails.DB;

/// <summary>
/// Running totals over three windows: all time (all days/providers), the current UTC month, and
/// this app run ("session"). Bytes are measured; tokens are a display heuristic.
/// </summary>
public sealed record TokenSavingsTotals(
    long BytesBefore,
    long BytesAfter,
    long SessionBytesSaved = 0,
    long MonthBytesBefore = 0,
    long MonthBytesAfter = 0)
{
    public long BytesSaved => BytesBefore - BytesAfter;

    /// <summary>
    /// ~4 bytes/token heuristic, derived only at read time (never persisted) so history reprices
    /// for free if a real tokenizer ever replaces the estimate.
    /// </summary>
    public long TokensSaved => BytesSaved / 4;

    /// <summary>
    /// Savings the table has gained since this process started — a delta, not a before/after pair,
    /// because it spans every process writing to state.db and not just this one's own requests.
    /// </summary>
    public long SessionTokensSaved => SessionBytesSaved / 4;

    public long MonthTokensSaved => (MonthBytesBefore - MonthBytesAfter) / 4;
}

/// <summary>
/// Persists the token-saver tally to state.db (one row per UTC day per provider) and serves the
/// running totals the UI displays.
///
/// The table is shared by every VibeRails process, and the ones that matter are the terminal-tab
/// children: each tab hosts its own LLM proxy, so the CLI's savings are recorded there, never in
/// the root backend that serves the dashboard. That makes the persisted table — not any single
/// process's memory — the only place where "what has this app saved" exists.
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
    /// Latest known totals: the most recent read of the persisted table, plus this process's own
    /// records that have not landed in it yet. Never waits on SQLite, so a caller that needs the
    /// other processes' newest rows awaits <see cref="RefreshAsync"/> first.
    /// </summary>
    TokenSavingsTotals GetTotals();

    /// <summary>
    /// Re-reads the persisted totals so this process sees what the other processes have saved.
    /// Await this before displaying a number; never call it from the relay hot path. Never throws:
    /// a failed refresh leaves the previous snapshot in place.
    /// </summary>
    Task RefreshAsync();
}

/// <summary>
/// The single place where token-saver persistence errors are allowed to be eaten. Everything else
/// about the write path is deliberately boring: connection-per-write like <see cref="Repository"/>,
/// plus <c>busy_timeout</c> (which <see cref="Repository"/> famously lacks) because this writer
/// races background embedding jobs for the same database file.
/// </summary>
public sealed class TokenSavingsStore : ITokenSavingsStore
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Lock _historyLoadScheduleLock = new();
    private readonly string _connectionString;
    private Task _historyLoadTask = Task.CompletedTask;
    private long _nextHistoryLoadAttemptUtcTicks;

    // Last totals read from the table. Absolute (replaced, never added to): this is how one
    // process sees the savings recorded by all the others.
    private long _dbBytesBefore;
    private long _dbBytesAfter;

    // This process's own records that the snapshot above cannot contain yet. Added by Record(),
    // removed the moment their row lands, so a total is always snapshot + unpersisted and counts
    // every request exactly once — including when there is no usable database at all and the
    // unpersisted side is the only tally there is.
    private long _unpersistedBytesBefore;
    private long _unpersistedBytesAfter;

    // Savings that already existed when this process started. "This session" is everything the
    // table has gained since, which is why the first read is queued ahead of every write.
    private long _baselineBytesSaved;
    private volatile bool _loaded;

    // Current-UTC-month tally. Guarded by its own lock (not Interlocked) because a month rollover
    // must swap the key and zero every counter as one unit.
    private readonly object _monthLock = new();
    private string _monthKey = "";
    private long _monthBytesBefore;
    private long _monthBytesAfter;
    private long _monthUnpersistedBytesBefore;
    private long _monthUnpersistedBytesAfter;

    /// <summary>Last fire-and-forget persist, observable so tests can await the write settling.</summary>
    internal Task LastPersist { get; private set; } = Task.CompletedTask;

    /// <summary>Testable clock; month-rollover behavior is unreachable under the real one.</summary>
    internal Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

    /// <summary>Backoff after an initial history-load failure.</summary>
    internal TimeSpan HistoryLoadRetryDelay { get; set; } = TimeSpan.FromSeconds(30);

    internal bool HistoryLoaded => _loaded;

    public TokenSavingsStore(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;

        // Start warming the persisted totals as soon as DI creates the singleton. The first API
        // read or proxy activity ping must never become the thread that pays this SQLite cost.
        EnsureHistoryLoadScheduled();
    }

    public void Record(string provider, int bytesBefore, int bytesAfter)
    {
        Interlocked.Add(ref _unpersistedBytesBefore, bytesBefore);
        Interlocked.Add(ref _unpersistedBytesAfter, bytesAfter);
        AddUnpersistedMonth(CurrentMonthKey(), bytesBefore, bytesAfter);
        LastPersist = PersistAsync(provider, bytesBefore, bytesAfter);
    }

    public TokenSavingsTotals GetTotals()
    {
        // A failed warm-up is retried after a backoff, but never by blocking this caller. Returning
        // a temporarily incomplete display tally is preferable to delaying an SSE response or
        // tying this API to the caller's synchronization context.
        EnsureHistoryLoadScheduled();

        var bytesBefore = Interlocked.Read(ref _dbBytesBefore) + Interlocked.Read(ref _unpersistedBytesBefore);
        var bytesAfter = Interlocked.Read(ref _dbBytesAfter) + Interlocked.Read(ref _unpersistedBytesAfter);
        var (monthBefore, monthAfter) = ReadMonth(CurrentMonthKey());

        // Clamped: a table that shrank under us (a reset, a restored backup) must read as "nothing
        // saved yet this run" rather than as a negative tally.
        var sessionBytesSaved = Math.Max(
            0, bytesBefore - bytesAfter - Interlocked.Read(ref _baselineBytesSaved));

        return new TokenSavingsTotals(bytesBefore, bytesAfter, sessionBytesSaved, monthBefore, monthAfter);
    }

    public Task RefreshAsync()
    {
        var refresh = PersistAsync(null, 0, 0);
        LastPersist = refresh;
        return refresh;
    }

    private void EnsureHistoryLoadScheduled()
    {
        if (_loaded || UtcNow().Ticks < Interlocked.Read(ref _nextHistoryLoadAttemptUtcTicks))
            return;

        lock (_historyLoadScheduleLock)
        {
            if (_loaded || !_historyLoadTask.IsCompleted ||
                UtcNow().Ticks < Interlocked.Read(ref _nextHistoryLoadAttemptUtcTicks))
            {
                return;
            }

            _historyLoadTask = PersistAsync(null, 0, 0);
            LastPersist = _historyLoadTask;
        }
    }

    private string CurrentMonthKey() => UtcNow().ToString("yyyy-MM");

    private void AddUnpersistedMonth(string monthKey, long bytesBefore, long bytesAfter)
    {
        lock (_monthLock)
        {
            if (!TryRollMonth(monthKey))
                return;

            _monthUnpersistedBytesBefore += bytesBefore;
            _monthUnpersistedBytesAfter += bytesAfter;
        }
    }

    /// <summary>Drops a record from the unpersisted tally once its row is in the table.</summary>
    private void SettleUnpersistedMonth(string monthKey, long bytesBefore, long bytesAfter)
    {
        lock (_monthLock)
        {
            // A rollover since the record was taken already zeroed those counters.
            if (_monthKey != monthKey)
                return;

            _monthUnpersistedBytesBefore = Math.Max(0, _monthUnpersistedBytesBefore - bytesBefore);
            _monthUnpersistedBytesAfter = Math.Max(0, _monthUnpersistedBytesAfter - bytesAfter);
        }
    }

    private void SetMonthSnapshot(string monthKey, long bytesBefore, long bytesAfter)
    {
        lock (_monthLock)
        {
            if (!TryRollMonth(monthKey))
                return;

            _monthBytesBefore = bytesBefore;
            _monthBytesAfter = bytesAfter;
        }
    }

    /// <summary>
    /// Points the month window at <paramref name="monthKey"/>, zeroing every counter when the
    /// month actually changes. Returns false for a stale key — keys sort chronologically
    /// ("yyyy-MM"), so an older one is a late arrival (e.g. a refresh landing just after a
    /// rollover) and must not resurrect last month. Caller holds <see cref="_monthLock"/>.
    /// </summary>
    private bool TryRollMonth(string monthKey)
    {
        if (string.CompareOrdinal(monthKey, _monthKey) < 0)
            return false;

        if (_monthKey != monthKey)
        {
            _monthKey = monthKey;
            _monthBytesBefore = 0;
            _monthBytesAfter = 0;
            _monthUnpersistedBytesBefore = 0;
            _monthUnpersistedBytesAfter = 0;
        }

        return true;
    }

    private (long Before, long After) ReadMonth(string monthKey)
    {
        lock (_monthLock)
        {
            // A stale key means nothing was recorded since the month rolled over: report zero
            // rather than last month's counters.
            return _monthKey == monthKey
                ? (_monthBytesBefore + _monthUnpersistedBytesBefore,
                   _monthBytesAfter + _monthUnpersistedBytesAfter)
                : (0L, 0L);
        }
    }

    /// <summary>
    /// Writes one record (when <paramref name="provider"/> is set) and re-reads the totals. Both
    /// halves share a connection because every write wants the fresh numbers anyway.
    /// </summary>
    private async Task PersistAsync(string? provider, int bytesBefore, int bytesAfter)
    {
        // Hop off the caller's thread FIRST: WaitAsync completes synchronously when the semaphore
        // is free (the common case), and everything below is synchronous SQLite — without this
        // yield, "fire-and-forget" would actually run the whole DB round-trip (up to a 5s
        // busy-wait under state.db contention) inline on the relay hot path before the SSE copy.
        await Task.Yield();

        // Serializing writes keeps concurrent relays from ever contending inside SQLite, and makes
        // the baseline ordering sound: the constructor's read is queued ahead of every write, so
        // "savings that predate this run" never includes one of this run's own requests.
        await _writeLock.WaitAsync();
        try
        {
            // When the initial load is backing off, drop this telemetry write instead of
            // persisting it first — a row written before the baseline is read would silently
            // become part of "what was already saved before this run started".
            if (!_loaded && UtcNow().Ticks < Interlocked.Read(ref _nextHistoryLoadAttemptUtcTicks))
                return;

            using var connection = new SqliteConnection(_connectionString);
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

            // Staged, not published: the baseline only counts if the whole refresh below succeeds,
            // so a half-read table leaves the store un-loaded and the retry starts clean.
            long? baselineBytesSaved = null;
            if (!_loaded)
            {
                var (historyBefore, historyAfter) = ReadTotals(connection);
                baselineBytesSaved = historyBefore - historyAfter;
            }

            var monthKey = CurrentMonthKey();
            if (provider is not null)
            {
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

                // Hand the record over to the table before re-reading it, never after: if the read
                // below throws while this record still sits in the unpersisted tally, the next
                // successful refresh would count it a second time. Settling early can only
                // undercount, and that repairs itself on the very next refresh.
                Interlocked.Add(ref _unpersistedBytesBefore, -bytesBefore);
                Interlocked.Add(ref _unpersistedBytesAfter, -bytesAfter);
                SettleUnpersistedMonth(monthKey, bytesBefore, bytesAfter);
            }

            var (totalBefore, totalAfter) = ReadTotals(connection);
            var (monthBefore, monthAfter) = ReadMonthTotals(connection, monthKey);

            Interlocked.Exchange(ref _dbBytesBefore, totalBefore);
            Interlocked.Exchange(ref _dbBytesAfter, totalAfter);
            SetMonthSnapshot(monthKey, monthBefore, monthAfter);

            if (baselineBytesSaved is not null)
            {
                Interlocked.Exchange(ref _baselineBytesSaved, baselineBytesSaved.Value);
                _loaded = true;
                Interlocked.Exchange(ref _nextHistoryLoadAttemptUtcTicks, 0);
            }
        }
        catch (Exception ex)
        {
            // Deliberate swallow (user decision): a lost tally increment is acceptable, a relay
            // slowed or failed by telemetry is not. This catch gates nothing but the tally itself.
            if (!_loaded)
            {
                var retryAtUtc = UtcNow().Add(HistoryLoadRetryDelay);
                Interlocked.Exchange(ref _nextHistoryLoadAttemptUtcTicks, retryAtUtc.Ticks);
                Log.Warning(
                    ex,
                    "Token-savings history load failed; serving in-memory totals and retrying after {RetryAtUtc}.",
                    retryAtUtc);
            }
            else
            {
                Log.Debug(ex, "Token-savings tally write failed; increment dropped.");
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static (long Before, long After) ReadTotals(SqliteConnection connection)
    {
        using var totals = connection.CreateCommand();
        totals.CommandText = SqlStrings.SelectTokenSavingsTotals;
        using var reader = totals.ExecuteReader();
        return reader.Read() ? (reader.GetInt64(0), reader.GetInt64(1)) : (0L, 0L);
    }

    private static (long Before, long After) ReadMonthTotals(SqliteConnection connection, string monthKey)
    {
        using var month = connection.CreateCommand();
        month.CommandText = SqlStrings.SelectTokenSavingsMonthTotals;
        month.Parameters.AddWithValue("$month", monthKey);
        using var reader = month.ExecuteReader();
        return reader.Read() ? (reader.GetInt64(0), reader.GetInt64(1)) : (0L, 0L);
    }
}
