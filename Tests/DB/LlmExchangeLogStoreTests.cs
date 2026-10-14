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
        bool truncated = false,
        string? sessionId = null,
        string? model = null,
        string? reasoningEffort = null,
        string? serviceTier = null) =>
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
            ElapsedMs: 1234,
            SessionId: sessionId,
            Model: model,
            ReasoningEffort: reasoningEffort,
            ServiceTier: serviceTier);

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
    public async Task Record_PersistsSessionIdWhenPresent()
    {
        using var store = new LlmExchangeLogStore(ConnectionString);

        store.Record(Exchange(sessionId: "0f8c2a11-5c82-4f6e-9d1a-3b7e8f2c4a55"));
        await store.WaitForDrainAsync();

        Assert.Equal("0f8c2a11-5c82-4f6e-9d1a-3b7e8f2c4a55", ReadSingle().SessionId);
    }

    [Fact]
    public async Task Record_LeavesSessionIdNullWhenAbsent()
    {
        // Requests launched before session attribution (or without a session) are the documented
        // NULL case — never a placeholder, never a guess.
        using var store = new LlmExchangeLogStore(ConnectionString);

        store.Record(Exchange());
        await store.WaitForDrainAsync();

        Assert.Null(ReadSingle().SessionId);
    }

    [Fact]
    public async Task Record_MigratesPreExistingDatabaseWithoutSessionIdColumn()
    {
        // The exact on-disk shape every existing proxy_exchanges.db has: created by the old
        // CREATE TABLE, no SessionId column. The first write after upgrade must ALTER it in
        // place rather than fail the insert (which would count as a drop — a hole in the log).
        using (var connection = new SqliteConnection(ConnectionString))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE ProxyExchanges (
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
                    ElapsedMs         INTEGER NOT NULL
                )
                """;
            command.ExecuteNonQuery();
        }

        using (var store = new LlmExchangeLogStore(ConnectionString))
        {
            store.Record(Exchange(sessionId: "sess-legacy-upgrade"));
            await store.WaitForDrainAsync();
            Assert.Equal(0, store.DroppedWrites);
        }

        Assert.Equal("sess-legacy-upgrade", ReadSingle().SessionId);
    }

    [Fact]
    public async Task Record_PersistsRequestedModelFields()
    {
        using var store = new LlmExchangeLogStore(ConnectionString);

        store.Record(Exchange(model: "gpt-6.1-sol", reasoningEffort: "xhigh", serviceTier: "priority"));
        await store.WaitForDrainAsync();

        var row = ReadSingle();
        Assert.Equal("gpt-6.1-sol", row.Model);
        Assert.Equal("xhigh", row.ReasoningEffort);
        Assert.Equal("priority", row.ServiceTier);
    }

    [Fact]
    public async Task Record_LeavesRequestedModelFieldsNullWhenAbsent()
    {
        // A body that is not JSON, was not buffered, or lacks a field records NULL, never a guess.
        using var store = new LlmExchangeLogStore(ConnectionString);

        store.Record(Exchange(model: "claude-opus-5-5"));
        await store.WaitForDrainAsync();

        var row = ReadSingle();
        Assert.Equal("claude-opus-5-5", row.Model);
        Assert.Null(row.ReasoningEffort);
        Assert.Null(row.ServiceTier);
    }

    [Fact]
    public async Task Record_UpgradesVersionOneDatabase_AndOlderInsertsStillWork()
    {
        // The on-disk shape every current proxy_exchanges.db has: proxy-exchanges/1 already applied,
        // so CreateTable never runs again and the new columns can only arrive through version 2.
        using (var connection = new SqliteConnection(ConnectionString))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $$"""
                CREATE TABLE ProxyExchanges (
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
                );
                CREATE INDEX IX_ProxyExchanges_CreatedUTC ON ProxyExchanges(CreatedUTC DESC);
                CREATE INDEX IX_ProxyExchanges_SessionId ON ProxyExchanges(SessionId);
                CREATE TABLE SchemaMigrations (
                    Component TEXT NOT NULL,
                    Version INTEGER NOT NULL CHECK (Version > 0),
                    AppliedUTC TEXT NOT NULL,
                    AppliedBy TEXT,
                    PRIMARY KEY (Component, Version)
                );
                INSERT INTO SchemaMigrations VALUES ('proxy-exchanges', 1, '2026-10-01T00:00:00Z', 'test');
                PRAGMA user_version={{LlmExchangeLogStore.Generation}};
                {{OlderBinaryInsert("pre-upgrade")}}
                """;
            command.ExecuteNonQuery();
        }

        using (var store = new LlmExchangeLogStore(ConnectionString))
        {
            store.Record(Exchange(sessionId: "sess-after-upgrade", model: "gpt-6.1-sol",
                reasoningEffort: "low", serviceTier: "priority"));
            await store.WaitForDrainAsync();
            Assert.Equal(0, store.DroppedWrites);
        }

        // A binary from before VIBE-155 can run beside this one: its INSERT never names the new
        // columns, and nullable is what lets it keep writing to the upgraded file.
        using (var connection = new SqliteConnection(ConnectionString))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = OlderBinaryInsert("post-upgrade");
            command.ExecuteNonQuery();
        }

        var rows = ReadAll().ToDictionary(row => row.SessionId ?? "");
        Assert.Equal(3, rows.Count);
        Assert.Equal(("gpt-6.1-sol", "low", "priority"), Fields(rows["sess-after-upgrade"]));
        // Rows written before the columns existed are never backfilled.
        Assert.Equal((null, null, null), Fields(rows["pre-upgrade"]));
        Assert.Equal((null, null, null), Fields(rows["post-upgrade"]));

        static (string?, string?, string?) Fields(ExchangeRow row) => (row.Model, row.ReasoningEffort, row.ServiceTier);
    }

    // The INSERT the store issued before VIBE-155, verbatim apart from literal values.
    private static string OlderBinaryInsert(string sessionId) => $"""
        INSERT INTO ProxyExchanges (
            Id, CreatedUTC, Provider, Method, Path, StatusCode,
            RequestBefore, RequestAfter, ResponseBody, ResponseTruncated,
            CharsBefore, CharsAfter, ResponseChars, ElapsedMs, SessionId)
        VALUES (
            '{Guid.NewGuid():D}', '2026-10-13T00:00:00.0000000Z', 'openai', 'POST', '/llm/openai/v1/responses', 200,
            'body', 'body', 'ok', 0, 4, 4, 2, 5, '{sessionId}');
        """;

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
        var rows = ReadAll();
        Assert.True(rows.Count == 1, "Expected exactly one exchange row.");
        return rows[0];
    }

    private List<ExchangeRow> ReadAll()
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Provider, Method, Path, StatusCode, RequestBefore, RequestAfter,
                   ResponseBody, ResponseTruncated, CharsBefore, CharsAfter, ElapsedMs, SessionId,
                   Model, ReasoningEffort, ServiceTier
            FROM ProxyExchanges ORDER BY rowid
            """;
        using var reader = command.ExecuteReader();
        var rows = new List<ExchangeRow>();
        while (reader.Read())
        {
            rows.Add(new ExchangeRow(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetInt32(4), reader.GetString(5), reader.GetString(6), reader.GetString(7),
                reader.GetBoolean(8), reader.GetInt32(9), reader.GetInt32(10), reader.GetInt32(11),
                NullableString(reader, 12), NullableString(reader, 13), NullableString(reader, 14),
                NullableString(reader, 15)));
        }
        return rows;
    }

    private static string? NullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private sealed record ExchangeRow(
        string Id, string Provider, string Method, string Path, int StatusCode,
        string RequestBefore, string RequestAfter, string ResponseBody, bool ResponseTruncated,
        int CharsBefore, int CharsAfter, int ElapsedMs, string? SessionId,
        string? Model, string? ReasoningEffort, string? ServiceTier);

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
