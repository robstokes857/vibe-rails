using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using VibeRails.Data.Sqlite;
using Serilog;
using TokenSaver;

namespace VibeRails.DB;

public sealed class LlmExchangeLogStore : ILlmExchangeLogStore, IDisposable
{
    internal const int DefaultQueueCapacity = 128;

    // Sized for a capture log that is always on rather than an opt-in diagnostic: the budget only
    // has to cover the burst while SQLite catches up, but every char over it is a hole in the very
    // dataset this table exists to build. 256 MB of retained text is affordable on a developer box
    // and buys enough headroom that dropping becomes evidence of a stuck writer, not of a busy one.
    internal const long DefaultMaxQueuedChars = 256L * 1024 * 1024;

    // Schema lives here rather than in SqlStrings because SqlStrings describes state.db, and this
    // table is deliberately not part of it. Keeping it local makes the separation hard to undo by
    // accident.
    private const string CreateTable = """
        CREATE TABLE IF NOT EXISTS ProxyExchanges (
            Id                TEXT    NOT NULL PRIMARY KEY,
            CreatedUTC        TEXT    NOT NULL,
            Provider          TEXT    NOT NULL,
            Method            TEXT    NOT NULL,
            Path              TEXT    NOT NULL,
            StatusCode        INTEGER NOT NULL,
            RequestBefore     TEXT    NOT NULL,
            RequestAfter      TEXT    NOT NULL,
            ResponseBody      TEXT    NOT NULL,
            ResponseTruncated INTEGER NOT NULL,
            CharsBefore       INTEGER NOT NULL,
            CharsAfter        INTEGER NOT NULL,
            ResponseChars     INTEGER NOT NULL,
            ElapsedMs         INTEGER NOT NULL,
            SessionId         TEXT    NULL
        )
        """;

    // Nullable on purpose: rows written before session attribution existed — and requests launched
    // without a session — have no value, and none is ever invented for them. Nullable is what makes
    // the column backward AND forward compatible: old binaries writing old-shape inserts against a
    // migrated database keep working, and new binaries against an unmigrated file get the column
    // added on first write.
    private const string AddSessionIdColumn =
        "ALTER TABLE ProxyExchanges ADD COLUMN SessionId TEXT";

    private const string CreateCreatedIndex =
        "CREATE INDEX IF NOT EXISTS IX_ProxyExchanges_CreatedUTC ON ProxyExchanges(CreatedUTC DESC)";

    private const string CreateSessionIdIndex =
        "CREATE INDEX IF NOT EXISTS IX_ProxyExchanges_SessionId ON ProxyExchanges(SessionId)";

    // Plain INSERT, not INSERT OR REPLACE: every Id is a fresh Guid, so a conflict would mean a
    // real bug upstream and should surface as one rather than silently overwrite a captured row.
    private const string Insert = """
        INSERT INTO ProxyExchanges (
            Id, CreatedUTC, Provider, Method, Path, StatusCode,
            RequestBefore, RequestAfter, ResponseBody, ResponseTruncated,
            CharsBefore, CharsAfter, ResponseChars, ElapsedMs, SessionId)
        VALUES (
            $id, $createdUTC, $provider, $method, $path, $statusCode,
            $requestBefore, $requestAfter, $responseBody, $responseTruncated,
            $charsBefore, $charsAfter, $responseChars, $elapsedMs, $sessionId)
        """;

    private readonly string _connectionString;
    private readonly Channel<QueuedExchange> _queue;
    private readonly long _maxQueuedChars;
    private readonly Lock _enqueueGate = new();
    private readonly Task _consumer;
    private long _queuedChars;
    private int _droppedWrites;
    private int _disposed;
    private bool _schemaReady;

    public LlmExchangeLogStore(string connectionString)
        : this(connectionString, DefaultQueueCapacity, DefaultMaxQueuedChars)
    {
    }

    internal LlmExchangeLogStore(string connectionString, int queueCapacity, long maxQueuedChars)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(queueCapacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxQueuedChars);

        _connectionString = connectionString;
        _maxQueuedChars = maxQueuedChars;
        // Wait mode is load-bearing next to TryWrite, not leftover configuration: it is what makes
        // TryWrite *fail* on a full queue so the drop is counted here. DropWrite would make TryWrite
        // succeed and discard silently, which is the one outcome this table cannot tolerate.
        _queue = Channel.CreateBounded<QueuedExchange>(new BoundedChannelOptions(queueCapacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        _consumer = Task.Run(ConsumeAsync);
    }

    /// <summary>Exchanges dropped rather than queued. Non-zero means the log has holes in it —
    /// which a later analysis must not mistake for the proxy having been idle.</summary>
    public int DroppedWrites => Volatile.Read(ref _droppedWrites);

    internal Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

    public void Record(LlmProxyExchange exchange)
    {
        try
        {
            var cost = (long)exchange.RequestBefore.Length
                + exchange.RequestAfter.Length
                + exchange.Response.Length;

            lock (_enqueueGate)
            {
                if (Volatile.Read(ref _disposed) != 0)
                    return;

                // Bounded by retained characters as well as item count: one exchange can be tens of
                // megabytes, so a queue depth limit alone would not bound memory.
                if (cost > _maxQueuedChars || _queuedChars > _maxQueuedChars - cost)
                {
                    NoteDrop(exchange.Id, "the retained-character budget is full", null);
                    return;
                }

                if (!_queue.Writer.TryWrite(new QueuedExchange(exchange, UtcNow(), cost)))
                {
                    NoteDrop(exchange.Id, "the write queue is full", null);
                    return;
                }

                _queuedChars += cost;
            }
        }
        catch (Exception ex)
        {
            NoteDrop(exchange.Id, "enqueue failed", ex);
        }
    }

    /// <summary>
    /// Counts a lost exchange and says so out loud. A hole in this table is invisible at read time —
    /// a gap looks exactly like an idle proxy — so this is the only place it can be noticed at all.
    /// Warning on the first drop and every hundredth after: enough to surface a stuck writer,
    /// bounded enough that a sustained stall cannot flood the log with one repeated line.
    /// </summary>
    private void NoteDrop(Guid exchangeId, string reason, Exception? ex)
    {
        var dropped = Interlocked.Increment(ref _droppedWrites);
        if (dropped != 1 && dropped % 100 != 0)
            return;

        Log.Warning(
            ex,
            "Proxy exchange {ExchangeId} dropped because {Reason}; {Dropped} lost so far. "
            + "The capture log now has gaps.",
            exchangeId,
            reason,
            dropped);
    }

    private async Task ConsumeAsync()
    {
        await foreach (var queued in _queue.Reader.ReadAllAsync())
        {
            try
            {
                await PersistAsync(queued);
            }
            catch (Exception ex)
            {
                NoteDrop(queued.Exchange.Id, "the database write failed", ex);
            }
            finally
            {
                lock (_enqueueGate)
                    _queuedChars -= queued.Cost;
            }
        }
    }

    private async Task PersistAsync(QueuedExchange queued)
    {
        var exchange = queued.Exchange;
        using var connection = await OpenAsync();
        using var insert = connection.CreateCommand();
        insert.CommandText = Insert;
        insert.Parameters.AddWithValue("$id", exchange.Id.ToString("D"));
        insert.Parameters.AddWithValue("$createdUTC", queued.CreatedUtc.ToString("o"));
        insert.Parameters.AddWithValue("$provider", exchange.Provider);
        insert.Parameters.AddWithValue("$method", exchange.Method);
        insert.Parameters.AddWithValue("$path", exchange.Path);
        insert.Parameters.AddWithValue("$statusCode", exchange.StatusCode);
        insert.Parameters.AddWithValue("$requestBefore", exchange.RequestBefore);
        insert.Parameters.AddWithValue("$requestAfter", exchange.RequestAfter);
        insert.Parameters.AddWithValue("$responseBody", exchange.Response);
        insert.Parameters.AddWithValue("$responseTruncated", exchange.ResponseTruncated ? 1 : 0);
        insert.Parameters.AddWithValue("$charsBefore", exchange.RequestBytesBefore);
        insert.Parameters.AddWithValue("$charsAfter", exchange.RequestBytesAfter);
        insert.Parameters.AddWithValue("$responseChars", exchange.Response.Length);
        insert.Parameters.AddWithValue("$elapsedMs", exchange.ElapsedMs);
        insert.Parameters.AddWithValue("$sessionId", (object?)exchange.SessionId ?? DBNull.Value);
        await insert.ExecuteNonQueryAsync();
    }

    /// <summary>proxy_exchanges.db schema generation (PRAGMA user_version); bumped only by breaking changes.</summary>
    internal const int Generation = 1;

    internal static void EnsureSchema(SqliteConnection connection)
    {
        SqliteMigrationRunner.RequireGenerationAtMost(connection, Generation, "proxy_exchanges.db");
        SqliteMigrationRunner.Apply(connection, "proxy-exchanges", 1, MigrationKind.Additive, (db, transaction) =>
        {
            SqliteSchema.Execute(db, transaction, CreateTable);
            SqliteSchema.AdoptStatement(db, transaction, AddSessionIdColumn);
            SqliteSchema.Execute(db, transaction, CreateCreatedIndex);
            SqliteSchema.Execute(db, transaction, CreateSessionIdIndex);
        });
        SqliteMigrationRunner.StampGeneration(connection, Generation);
    }

    private async Task<SqliteConnection> OpenAsync()
    {
        var connection = await SqliteConnectionFactory.OpenAsync(_connectionString);
        try
        {
            if (!_schemaReady)
            {
                EnsureSchema(connection);
                _schemaReady = true;
            }
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        lock (_enqueueGate)
            _queue.Writer.TryComplete();
        try
        {
            _consumer.Wait(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Proxy exchange writer did not stop cleanly.");
        }
    }

    /// <summary>Test hook: completes once every queued exchange has been written.</summary>
    internal async Task WaitForDrainAsync()
    {
        while (Volatile.Read(ref _queuedChars) > 0)
            await Task.Delay(10);
    }

    private sealed record QueuedExchange(LlmProxyExchange Exchange, DateTime CreatedUtc, long Cost);
}
