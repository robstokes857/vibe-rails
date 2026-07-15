using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using Serilog;
using TokenSaver;
using TokenSaver.Pipeline;

namespace VibeRails.DB;

/// <summary>
/// One capture as the list view needs it. The absence of RawText/CompressedText is the point: the
/// table is uncapped and a single row can hold megabytes, so the big strings are only ever fetched
/// one capture at a time via <see cref="ICompressionCaptureStore.GetAsync"/>.
/// </summary>
public sealed record CompressionCaptureSummary(
    Guid Id,
    DateTime CreatedUtc,
    string Provider,
    string ToolName,
    string? Command,
    int CharsBefore,
    int CharsAfter,
    bool Changed,
    bool RewriteAccepted);

/// <summary>
/// One capture in full — the unit the compress runbook judges, reconstituted from state.db.
///
/// <see cref="RawText"/> is byte-for-byte what the pipeline saw, which is what makes the what-if
/// preview real: feeding it back through a different plan is the same operation the proxy performed,
/// not a simulation of it. Persisting a truncated or normalized RawText would quietly turn the
/// preview into a lie, which is why the table stores it verbatim and uncapped.
/// </summary>
public sealed record CompressionCaptureDetail(
    Guid Id,
    DateTime CreatedUtc,
    string Provider,
    string ToolName,
    string? Command,
    string RawText,
    string CompressedText,
    int CharsBefore,
    int CharsAfter,
    bool Changed,
    bool RewriteAccepted,
    IReadOnlyList<StageTrace> Trace,
    IReadOnlyList<string> EnabledIds);

/// <summary>
/// Persists raw before/after compression captures to state.db and serves them back to the capture
/// view and the what-if preview.
/// </summary>
public interface ICompressionCaptureStore
{
    /// <summary>
    /// Records one tool_result's compression. Never blocks and never throws: the row is written
    /// fire-and-forget with failures swallowed. This is called from the LLM relay's hot path, where
    /// a dropped capture is a bad afternoon and a blocked relay is a broken product.
    /// </summary>
    void Record(CompressionCapture capture);

    /// <summary>
    /// Newest-first page of capture summaries. <paramref name="take"/> is clamped to
    /// <see cref="CompressionCaptureStore.MaxTake"/> — the table has no retention policy, so an
    /// unbounded take is a request to load the entire capture history into memory.
    /// </summary>
    Task<List<CompressionCaptureSummary>> ListAsync(int take, int skip, CancellationToken cancellationToken);

    /// <summary>One capture in full, or null when <paramref name="id"/> is unknown.</summary>
    Task<CompressionCaptureDetail?> GetAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes every capture and returns the row count. Exists because the table is deliberately
    /// uncapped: with no retention policy, an explicit reset is the only eviction there is.
    /// </summary>
    Task<int> ClearAsync(CancellationToken cancellationToken);
}

/// <summary>
/// JSON shapes for the two array columns. Source-generated because the proxy publishes AOT, where
/// the reflection-based <see cref="JsonSerializer"/> overloads fail at runtime rather than at build.
///
/// <see cref="StageOutcome"/> is written as a string on purpose: these rows outlive the build that
/// wrote them, and an ordinal would silently re-read history if a value is ever inserted into the
/// enum. Same durability argument the catalog makes for stage ids.
/// </summary>
[JsonSerializable(typeof(List<StageTrace>))]
[JsonSerializable(typeof(List<string>))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true)]
internal partial class CompressionCaptureJsonContext : JsonSerializerContext
{
}

/// <summary>
/// The single place where capture persistence errors are allowed to be eaten, for the same reason
/// <see cref="TokenSavingsStore"/> eats tally errors: this writer sits on the LLM relay's hot path
/// and nothing it does is worth slowing or failing a request over. The write path is deliberately
/// the same boring shape — connection-per-write and <c>busy_timeout</c> (which <see cref="Repository"/>
/// famously lacks) because it races background embedding jobs for the same database file. A single
/// command consumer orders inserts, re-sight counts, clears, and test barriers. Its queue is bounded
/// by both item count and retained character count; overload drops diagnostics instead of turning a
/// locked database into an out-of-memory failure. Hashing also runs on that consumer, not the relay.
///
/// Reads (<see cref="ListAsync"/>, <see cref="GetAsync"/>, <see cref="ClearAsync"/>) do NOT swallow.
/// They are user-initiated requests with somewhere to report a failure, and a capture view that
/// silently renders an empty list over a broken database is worse than one that returns a 500.
/// </summary>
public sealed class CompressionCaptureStore : ICompressionCaptureStore, IDisposable
{
    internal const int MaxTake = 200;
    internal const int SeenHashCap = 20_000;
    internal const int DefaultQueueCapacity = 64;
    internal const long DefaultMaxQueuedChars = 24L * 1024 * 1024;
    private const string ContentHashVersion = "3";

    private readonly string _connectionString;
    private readonly Channel<WriterCommand> _commands;
    private readonly long _maxQueuedChars;
    private readonly HashSet<string> _seen = [];
    private readonly Lock _enqueueGate = new();
    private readonly SemaphoreSlim _controlGate = new(1, 1);
    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    private readonly Task _consumer;
    private bool _controlPending;
    private bool _schemaReady;
    private int _failedWrites;
    private int _disposed;
    private long _queuedChars;

    public CompressionCaptureStore(string connectionString) :
        this(connectionString, DefaultQueueCapacity, DefaultMaxQueuedChars)
    {
    }

    internal CompressionCaptureStore(string connectionString, int queueCapacity, long maxQueuedChars)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(queueCapacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxQueuedChars);
        _connectionString = connectionString;
        _maxQueuedChars = maxQueuedChars;
        _commands = Channel.CreateBounded<WriterCommand>(new BoundedChannelOptions(queueCapacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        _consumer = Task.Run(ConsumeAsync);
    }

    public int FailedWrites => Volatile.Read(ref _failedWrites);

    internal Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

    /// <summary>A barrier in the writer stream. Unlike observing channel count or a "last task",
    /// this cannot overtake a command the consumer has already dequeued.</summary>
    internal async Task WaitForDrainAsync()
    {
        var completion = NewCompletion<bool>();
        await EnqueueControlAsync(new BarrierCommand(completion), CancellationToken.None);
        await completion.Task;
    }

    public void Record(CompressionCapture capture)
    {
        try
        {
            var queuedChars = CaptureCharCost(capture);
            lock (_enqueueGate)
            {
                if (Volatile.Read(ref _disposed) != 0)
                    return;

                // A clear/barrier waiting for capacity owns the next queue slot. New captures are
                // deliberately dropped until it lands, which preserves the control command's
                // ordering without ever blocking the LLM request thread.
                if (_controlPending
                    || queuedChars > _maxQueuedChars
                    || _queuedChars > _maxQueuedChars - queuedChars)
                {
                    Interlocked.Increment(ref _failedWrites);
                    return;
                }

                var command = new CaptureCommand(capture, UtcNow(), queuedChars);
                if (!_commands.Writer.TryWrite(command))
                {
                    Interlocked.Increment(ref _failedWrites);
                    return;
                }

                _queuedChars += queuedChars;
            }
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _failedWrites);
            Log.Debug(ex, "Compression capture enqueue failed; capture {CaptureId} dropped.", capture.Id);
        }
    }

    private static long CaptureCharCost(CompressionCapture capture) =>
        (long)capture.RawText.Length
        + capture.CompressedText.Length
        + (capture.Command?.Length ?? 0);

    /// <summary>
    /// Enqueues a non-droppable ordering command. While it waits for bounded-channel capacity,
    /// Record drops new captures rather than letting them jump ahead or blocking the relay.
    /// </summary>
    private async Task EnqueueControlAsync(WriterCommand command, CancellationToken cancellationToken)
    {
        await _controlGate.WaitAsync(cancellationToken);
        try
        {
            lock (_enqueueGate)
            {
                if (Volatile.Read(ref _disposed) != 0)
                    throw new ObjectDisposedException(nameof(CompressionCaptureStore));
                _controlPending = true;
            }

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (_enqueueGate)
                {
                    if (Volatile.Read(ref _disposed) != 0)
                        throw new ObjectDisposedException(nameof(CompressionCaptureStore));
                    if (_commands.Writer.TryWrite(command))
                    {
                        _controlPending = false;
                        return;
                    }
                }

                if (!await _commands.Writer.WaitToWriteAsync(cancellationToken))
                    throw new ObjectDisposedException(nameof(CompressionCaptureStore));
            }
        }
        finally
        {
            lock (_enqueueGate)
                _controlPending = false;
            _controlGate.Release();
        }
    }

    /// <summary>
    /// Dedupe includes both sides of the experiment and its trace. Stage ids are stable across
    /// builds, but implementations are allowed to improve; two builds producing different output
    /// or attribution from the same input must remain two independently judgeable captures.
    /// Length-prefixing makes the material unambiguous even when tool output contains NULs.
    /// </summary>
    private static string ContentHash(CompressionCapture capture)
    {
        var material = new StringBuilder(capture.RawText.Length + capture.CompressedText.Length + 256);
        // A named hash-contract version makes future semantic changes explicit and ensures rows
        // created under an older dedupe definition do not silently absorb newer diagnostics.
        Append(ContentHashVersion);
        Append(capture.Provider);
        Append(capture.ToolName);
        Append(capture.Command);
        foreach (var id in capture.EnabledIds.OrderBy(id => id, StringComparer.Ordinal))
            Append(id);
        Append(capture.RawText);
        Append(capture.CompressedText);
        Append(capture.RewriteAccepted ? "accepted" : "not-accepted");
        foreach (var stage in capture.Trace)
        {
            Append(stage.StageId);
            Append(stage.Outcome.ToString());
            Append(stage.CharsRemoved.ToString(CultureInfo.InvariantCulture));
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material.ToString())));

        void Append(string? value)
        {
            if (value is null)
            {
                material.Append("-1:");
                return;
            }
            material.Append(value.Length).Append(':').Append(value);
        }
    }

    public async Task<List<CompressionCaptureSummary>> ListAsync(
        int take, int skip, CancellationToken cancellationToken)
    {
        using var connection = await OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = SqlStrings.SelectCompressionCaptureSummaries;
        command.Parameters.AddWithValue("$take", Math.Clamp(take, 1, MaxTake));
        command.Parameters.AddWithValue("$skip", Math.Max(skip, 0));

        var summaries = new List<CompressionCaptureSummary>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            summaries.Add(new CompressionCaptureSummary(
                Guid.Parse(reader.GetString(0)),
                ParseUtc(reader.GetString(1)),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetInt32(5),
                reader.GetInt32(6),
                reader.GetBoolean(7),
                reader.GetBoolean(8)));
        }
        return summaries;
    }

    public async Task<CompressionCaptureDetail?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        using var connection = await OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = SqlStrings.SelectCompressionCaptureById;
        command.Parameters.AddWithValue("$id", id.ToString("D"));

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        return new CompressionCaptureDetail(
            Guid.Parse(reader.GetString(0)),
            ParseUtc(reader.GetString(1)),
            reader.GetString(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetInt32(7),
            reader.GetInt32(8),
            reader.GetBoolean(9),
            reader.GetBoolean(10),
            JsonSerializer.Deserialize(
                reader.GetString(12), CompressionCaptureJsonContext.Default.ListStageTrace) ?? [],
            JsonSerializer.Deserialize(
                reader.GetString(11), CompressionCaptureJsonContext.Default.ListString) ?? []);
    }

    public async Task<int> ClearAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var completion = NewCompletion<int>();
        await EnqueueControlAsync(new ClearCommand(completion), cancellationToken);
        return await completion.Task.WaitAsync(cancellationToken);
    }

    private async Task ConsumeAsync()
    {
        await foreach (var command in _commands.Reader.ReadAllAsync())
        {
            switch (command)
            {
                case CaptureCommand capture:
                    try
                    {
                        await ProcessCaptureAsync(capture);
                    }
                    finally
                    {
                        lock (_enqueueGate)
                            _queuedChars -= capture.QueuedChars;
                    }
                    break;
                case ClearCommand clear:
                    await ClearAsync(clear);
                    break;
                case BarrierCommand barrier:
                    barrier.Completion.TrySetResult(true);
                    break;
            }
        }
    }

    private async Task ProcessCaptureAsync(CaptureCommand queued)
    {
        string? contentHash = null;
        try
        {
            // Hashing copies and UTF-8 encodes the raw payload, so it belongs on this background
            // consumer, never in Record on the LLM request thread.
            contentHash = ContentHash(queued.Capture);
            if (_seen.Contains(contentHash))
            {
                var touched = await TouchAsync(contentHash);
                if (touched > 0)
                    return;

                // Another VibeRails process can clear the shared table without clearing this
                // process's in-memory cache. A zero-row touch is proof the cache is stale; fall
                // back to the full upsert so capture recording resumes immediately.
                _seen.Remove(contentHash);
            }

            if (_seen.Count >= SeenHashCap)
                _seen.Clear();
            _seen.Add(contentHash);
            await PersistAsync(queued, contentHash);
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _failedWrites);
            if (contentHash is not null)
                _seen.Remove(contentHash);
            Log.Debug(ex, "Compression capture write failed; capture {CaptureId} dropped.", queued.Capture.Id);
        }
    }

    private async Task PersistAsync(CaptureCommand queued, string contentHash)
    {
        var capture = queued.Capture;
        var trace = JsonSerializer.Serialize(
            [.. capture.Trace], CompressionCaptureJsonContext.Default.ListStageTrace);
        var enabledIds = JsonSerializer.Serialize(
            [.. capture.EnabledIds], CompressionCaptureJsonContext.Default.ListString);

        using var connection = await OpenAsync(CancellationToken.None);
        using var insert = connection.CreateCommand();
        insert.CommandText = SqlStrings.UpsertCompressionCapture;
        insert.Parameters.AddWithValue("$id", capture.Id.ToString("D"));
        insert.Parameters.AddWithValue("$createdUTC", queued.CreatedUtc.ToString("o"));
        insert.Parameters.AddWithValue("$provider", capture.Provider);
        insert.Parameters.AddWithValue("$toolName", capture.ToolName);
        insert.Parameters.AddWithValue("$command", (object?)capture.Command ?? DBNull.Value);
        insert.Parameters.AddWithValue("$rawText", capture.RawText);
        insert.Parameters.AddWithValue("$compressedText", capture.CompressedText);
        insert.Parameters.AddWithValue("$charsBefore", capture.CharsBefore);
        insert.Parameters.AddWithValue("$charsAfter", capture.CharsAfter);
        insert.Parameters.AddWithValue("$changed", capture.Changed ? 1 : 0);
        insert.Parameters.AddWithValue("$rewriteAccepted", capture.RewriteAccepted ? 1 : 0);
        insert.Parameters.AddWithValue("$enabledIds", enabledIds);
        insert.Parameters.AddWithValue("$trace", trace);
        insert.Parameters.AddWithValue("$contentHash", contentHash);
        await insert.ExecuteNonQueryAsync();
    }

    private async Task<int> TouchAsync(string contentHash)
    {
        using var connection = await OpenAsync(CancellationToken.None);
        using var update = connection.CreateCommand();
        update.CommandText = SqlStrings.TouchCompressionCapture;
        update.Parameters.AddWithValue("$contentHash", contentHash);
        update.Parameters.AddWithValue("$delta", 1);
        return await update.ExecuteNonQueryAsync();
    }

    private async Task ClearAsync(ClearCommand queued)
    {
        try
        {
            // This command is ordered after every capture accepted before ClearAsync and before
            // every capture accepted after it. Clearing here (on the sole consumer) keeps the
            // in-memory dedupe view aligned with that exact database boundary.
            _seen.Clear();
            using var connection = await OpenAsync(CancellationToken.None);
            using var delete = connection.CreateCommand();
            delete.CommandText = SqlStrings.DeleteAllCompressionCaptures;
            queued.Completion.TrySetResult(await delete.ExecuteNonQueryAsync());
        }
        catch (Exception ex)
        {
            queued.Completion.TrySetException(ex);
        }
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            using (var pragma = connection.CreateCommand())
            {
                pragma.CommandText = "PRAGMA busy_timeout = 5000;";
                await pragma.ExecuteNonQueryAsync(cancellationToken);
            }
            await EnsureSchemaAsync(connection, cancellationToken);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private async Task EnsureSchemaAsync(
        SqliteConnection connection, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _schemaReady))
            return;

        await _schemaLock.WaitAsync(cancellationToken);
        try
        {
            if (_schemaReady)
                return;

            await ExecuteAsync(SqlStrings.CreateCompressionCapturesTable);
            await AddColumnIfMissingAsync("ContentHash", SqlStrings.AddCompressionCaptureContentHashColumn);
            await AddColumnIfMissingAsync("SeenCount", SqlStrings.AddCompressionCaptureSeenCountColumn);
            await AddColumnIfMissingAsync(
                "RewriteAccepted", SqlStrings.AddCompressionCaptureRewriteAcceptedColumn);
            await ExecuteAsync(SqlStrings.CreateCompressionCapturesCreatedIndex);
            await ExecuteAsync(SqlStrings.CreateCompressionCapturesHashIndex);
            Volatile.Write(ref _schemaReady, true);
        }
        finally
        {
            _schemaLock.Release();
        }

        async Task AddColumnIfMissingAsync(string column, string migration)
        {
            if (await HasColumnAsync(column))
                return;
            try
            {
                await ExecuteAsync(migration);
            }
            catch (SqliteException ex) when (
                ex.Message.Contains("duplicate column", StringComparison.OrdinalIgnoreCase))
            {
                // Another process migrated the shared database after our PRAGMA read.
            }
        }

        async Task<bool> HasColumnAsync(string column)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA table_info(CompressionCaptures);";
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (reader.GetString(1).Equals(column, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        async Task ExecuteAsync(string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        lock (_enqueueGate)
            _commands.Writer.TryComplete();
        try
        {
            _consumer.Wait(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Compression capture writer did not stop cleanly.");
        }
    }

    private static TaskCompletionSource<T> NewCompletion<T>() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private abstract record WriterCommand;
    private sealed record CaptureCommand(
        CompressionCapture Capture, DateTime CreatedUtc, long QueuedChars) : WriterCommand;
    private sealed record ClearCommand(TaskCompletionSource<int> Completion) : WriterCommand;
    private sealed record BarrierCommand(TaskCompletionSource<bool> Completion) : WriterCommand;

    private static DateTime ParseUtc(string value) =>
        DateTime.ParseExact(value, "o", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
