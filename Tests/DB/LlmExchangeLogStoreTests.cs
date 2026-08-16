using Microsoft.Data.Sqlite;
using TokenSaver;
using VibeRails.DB;
using Xunit;

namespace Tests.DB;

/// <summary>
/// Pins the exchange log's contract: whole request/response pairs land in their own database file,
/// the writer never throws or blocks the relay, and an overloaded queue drops records rather than
/// growing without bound — because one exchange can be tens of megabytes.
/// </summary>
public sealed class LlmExchangeLogStoreTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"viberails-exchanges-test-{Guid.NewGuid():N}.db");

    private string ConnectionString => $"Data Source={_dbPath};Mode=ReadWriteCreate";

    private static LlmProxyExchange Exchange(
        string requestBefore = "{\"before\":1}",
        string requestAfter = "{\"after\":1}",
        string response = "data: {}\n\n",
        bool truncated = false) =>
        new(
            Guid.NewGuid(),
            "anthropic",
            "POST",
            "/llm/anthropic/v1/messages",
            200,
            requestBefore,
            requestAfter,
            response,
            truncated,
            ElapsedMs: 1234);

    [Fact]
    public async Task Record_WritesTheWholeExchange()
    {
        using var store = new LlmExchangeLogStore(ConnectionString);
        var exchange = Exchange();

        store.Record(exchange);
        await store.WaitForDrainAsync();

        var row = ReadSingle();
        Assert.Equal(exchange.Id.ToString("D"), row.Id);
        Assert.Equal("anthropic", row.Provider);
        Assert.Equal("POST", row.Method);
        Assert.Equal("/llm/anthropic/v1/messages", row.Path);
        Assert.Equal(200, row.StatusCode);
        Assert.Equal("{\"before\":1}", row.RequestBefore);
        Assert.Equal("{\"after\":1}", row.RequestAfter);
        Assert.Equal("data: {}\n\n", row.ResponseBody);
        Assert.False(row.ResponseTruncated);
        Assert.Equal(1234, row.ElapsedMs);
        // Sizes are derived at write time so a query can rank by payload without reading the text.
        Assert.Equal(12, row.CharsBefore);
        Assert.Equal(11, row.CharsAfter);
    }

    [Fact]
    public async Task Record_PassthroughRequest_StillWritesARow()
    {
        using var store = new LlmExchangeLogStore(ConnectionString);

        // A request the saver declined has empty before/after. The row is the evidence that the
        // pipeline saw this traffic and did nothing — absence would read as "no traffic".
        store.Record(Exchange(requestBefore: "", requestAfter: "", response: "ok"));
        await store.WaitForDrainAsync();

        var row = ReadSingle();
        Assert.Equal(string.Empty, row.RequestBefore);
        Assert.Equal(0, row.CharsBefore);
        Assert.Equal("ok", row.ResponseBody);
    }

    [Fact]
    public async Task Record_TruncatedResponse_IsMarked()
    {
        using var store = new LlmExchangeLogStore(ConnectionString);

        store.Record(Exchange(response: "partial", truncated: true));
        await store.WaitForDrainAsync();

        Assert.True(ReadSingle().ResponseTruncated);
    }

    [Fact]
    public void Record_OversizeExchange_IsDroppedNotQueued()
    {
        using var store = new LlmExchangeLogStore(ConnectionString, queueCapacity: 4, maxQueuedChars: 64);

        store.Record(Exchange(response: new string('x', 500)));

        // Dropping is the designed outcome: memory is bounded by retained characters, and a
        // diagnostic is never worth an out-of-memory failure on the relay's hot path.
        Assert.Equal(1, store.DroppedWrites);
        Assert.Equal(0, RowCount());
    }

    [Fact]
    public async Task Record_UnusableDatabase_NeverThrows()
    {
        using var store = new LlmExchangeLogStore(
            $"Data Source={Path.Combine(_dbPath, "nested", "into-a-file.db")};Mode=ReadWrite");

        store.Record(Exchange());
        await store.WaitForDrainAsync();

        Assert.Equal(1, store.DroppedWrites);
    }

    private int RowCount()
    {
        if (!File.Exists(_dbPath))
            return 0;
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM ProxyExchanges";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private ExchangeRow ReadSingle()
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Provider, Method, Path, StatusCode, RequestBefore, RequestAfter,
                   ResponseBody, ResponseTruncated, CharsBefore, CharsAfter, ElapsedMs
            FROM ProxyExchanges
            """;
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read(), "Expected exactly one exchange row.");
        var row = new ExchangeRow(
            reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.GetInt32(4), reader.GetString(5), reader.GetString(6), reader.GetString(7),
            reader.GetBoolean(8), reader.GetInt32(9), reader.GetInt32(10), reader.GetInt32(11));
        Assert.False(reader.Read(), "Expected exactly one exchange row.");
        return row;
    }

    private sealed record ExchangeRow(
        string Id, string Provider, string Method, string Path, int StatusCode,
        string RequestBefore, string RequestAfter, string ResponseBody, bool ResponseTruncated,
        int CharsBefore, int CharsAfter, int ElapsedMs);

    public void Dispose()
    {
        // Scoped to this class's connection string: a process-wide ClearAllPools() disposes handles
        // out from under DB test classes running in parallel.
        SqliteConnection.ClearPool(new SqliteConnection(ConnectionString));
        foreach (var path in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // Best-effort temp cleanup; the OS temp dir owns the leftovers.
            }
        }
    }
}
