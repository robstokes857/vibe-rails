using System.Text.Json;
using Microsoft.Data.Sqlite;
using VibeRails.Data.Sqlite;
using Xunit;

namespace Tests.DB;

public sealed class ProxyExchangeArchiveReaderTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"viberails-proxy-archive-{Guid.NewGuid():N}.db");

    [Fact]
    public async Task SessionEnvelopeComposesCompleteProxyArrayAfterOrdinarySessionFields()
    {
        CreateSchema();
        var sessionId = Guid.NewGuid().ToString("D");
        Insert("exchange", sessionId, "original 🚀", "transformed", "response", false);
        var connectionString = new SqliteConnectionStringBuilder { DataSource = _path + ".state", Pooling = false }.ToString();
        var repository = new VibeRails.DB.Repository(connectionString,
            proxyArchiveReader: new SqliteProxyExchangeArchiveReader(_path));
        await repository.CreateSessionAsync(sessionId, "codex", null, Path.GetTempPath(), Environment.ProcessId);
        await repository.InsertUserInputAsync(sessionId, 1, "preserved prompt", null);
        await repository.CompleteSessionAsync(sessionId, 0);
        await using var output = new MemoryStream();
        var descriptor = await repository.WriteSessionExportAsync(sessionId, output, TestContext.Current.CancellationToken);
        Assert.Equal(2, descriptor!.SchemaVersion);
        using var document = JsonDocument.Parse(output.ToArray());
        var root = document.RootElement;
        Assert.Equal("preserved prompt", root.GetProperty("userInputs")[0].GetProperty("inputText").GetString());
        Assert.Equal("included", root.GetProperty("proxyCoverage").GetProperty("status").GetString());
        Assert.Equal(1, root.GetProperty("proxyCoverage").GetProperty("count").GetInt32());
        Assert.Equal("original 🚀", root.GetProperty("proxyExchanges")[0].GetProperty("requestBefore").GetString());
        Assert.Equal(sessionId, root.GetProperty("proxyExchanges")[0].GetProperty("sessionId").GetString());
    }

    [Fact]
    public async Task StreamsExactUnicodeBodiesAndOnlyActualSessionMatches()
    {
        CreateSchema();
        var session = Guid.NewGuid().ToString("D");
        var text = "\uFEFF" + new string('x', 16382) + "🚀\"\\\n\tα" + new string('y', 200_000);
        Insert("first", session, text, "", "data: {\"tool\":\"雪\"}\n\n", true);
        Insert("other-session", Guid.NewGuid().ToString("D"), "other", "other", "other", false);
        Insert("unattributed", null, "unknown", "unknown", "unknown", false);
        Insert("last", session, "", text, "", false);
        var reader = new SqliteProxyExchangeArchiveReader(_path);
        await using var prepared = await reader.PrepareAsync(session, TestContext.Current.CancellationToken);
        Assert.Equal("included", prepared.Status);
        Assert.Equal(2, prepared.Count);
        Assert.NotNull(prepared.SnapshotUtc);
        Assert.NotNull(prepared.Content);
        using var document = await JsonDocument.ParseAsync(prepared.Content!, cancellationToken: TestContext.Current.CancellationToken);
        var rows = document.RootElement;
        Assert.Equal(2, rows.GetArrayLength());
        Assert.Equal("first", rows[0].GetProperty("id").GetString());
        Assert.Equal(text, rows[0].GetProperty("requestBefore").GetString());
        Assert.Equal("", rows[0].GetProperty("requestAfter").GetString());
        Assert.Equal("data: {\"tool\":\"雪\"}\n\n", rows[0].GetProperty("responseBody").GetString());
        Assert.True(rows[0].GetProperty("responseTruncated").GetBoolean());
        Assert.Equal("last", rows[1].GetProperty("id").GetString());
        Assert.Equal(text, rows[1].GetProperty("requestAfter").GetString());
        Assert.Equal(text.Length, rows[0].GetProperty("charsBefore").GetInt32());
    }

    [Fact]
    public async Task ConcurrentWalWriterDoesNotMixVersionsWithinArchiveSnapshot()
    {
        CreateSchema();
        using (var setup = Open())
        using (var wal = setup.CreateCommand())
        {
            wal.CommandText = "PRAGMA journal_mode=WAL;";
            Assert.Equal("wal", wal.ExecuteScalar());
        }
        var session = Guid.NewGuid().ToString("D");
        var before = "before:" + new string('x', 256 * 1024);
        var after = "after:" + new string('y', 256 * 1024);
        for (var i = 0; i < 12; i++) Insert("row-" + i, session, before, before, "response", false);
        using var start = new ManualResetEventSlim();
        var cancellationToken = TestContext.Current.CancellationToken;
        var writer = Task.Run(() =>
        {
            start.Wait(TestContext.Current.CancellationToken);
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE ProxyExchanges SET RequestBefore=$text, RequestAfter=$text;";
            update.Parameters.AddWithValue("$text", after);
            var changed = update.ExecuteNonQuery();
            transaction.Commit();
            return changed;
        }, cancellationToken);
        var reading = Task.Run(async () =>
        {
            start.Set();
            await using var prepared = await new SqliteProxyExchangeArchiveReader(_path)
                .PrepareAsync(session, TestContext.Current.CancellationToken);
            Assert.Equal("included", prepared.Status);
            using var document = await JsonDocument.ParseAsync(prepared.Content!, cancellationToken: TestContext.Current.CancellationToken);
            var rows = document.RootElement.EnumerateArray().ToArray();
            Assert.Equal(12, rows.Length);
            var snapshot = rows[0].GetProperty("requestBefore").GetString();
            Assert.True(snapshot == before || snapshot == after);
            Assert.All(rows, row =>
            {
                Assert.Equal(snapshot, row.GetProperty("requestBefore").GetString());
                Assert.Equal(snapshot, row.GetProperty("requestAfter").GetString());
            });
        }, cancellationToken);
        await Task.WhenAll(reading, writer);
        Assert.Equal(12, await writer);
    }

    [Fact]
    public async Task MissingSourceIsUnavailableButVerifiedNoMatchesIsEmpty()
    {
        var reader = new SqliteProxyExchangeArchiveReader(_path);
        await using var missing = await reader.PrepareAsync("session", TestContext.Current.CancellationToken);
        Assert.Equal("unavailable", missing.Status);
        Assert.Null(missing.Count);
        Assert.Null(missing.Content);
        Assert.False(File.Exists(_path));
        CreateSchema();
        await using var empty = await reader.PrepareAsync("session", TestContext.Current.CancellationToken);
        Assert.Equal("empty", empty.Status);
        Assert.Equal(0, empty.Count);
        Assert.NotNull(empty.SnapshotUtc);
        Assert.Null(empty.Content);
    }

    [Fact]
    public async Task BrokenOptionalSourceReturnsUnavailableWithoutPartialArray()
    {
        using (var connection = Open())
        {
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE ProxyExchanges(SessionId TEXT); INSERT INTO ProxyExchanges VALUES ('session');";
            command.ExecuteNonQuery();
        }
        var reader = new SqliteProxyExchangeArchiveReader(_path);
        await using var result = await reader.PrepareAsync("session", TestContext.Current.CancellationToken);
        Assert.Equal("unavailable", result.Status);
        Assert.Null(result.Content);
        Assert.Null(result.Count);
    }

    [Fact]
    public async Task CancellationDoesNotBecomeMissingCoverage()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new SqliteProxyExchangeArchiveReader(_path).PrepareAsync("session", cancellation.Token));
    }

    private SqliteConnection Open() => SqliteConnectionFactory.Open(
        new SqliteConnectionStringBuilder { DataSource = _path, Pooling = false }.ToString());

    private void CreateSchema()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE ProxyExchanges (
                Id TEXT PRIMARY KEY, SessionId TEXT, CreatedUTC TEXT, Provider TEXT, Method TEXT,
                Path TEXT, StatusCode INTEGER, RequestBefore TEXT, RequestAfter TEXT, ResponseBody TEXT,
                ResponseTruncated INTEGER, CharsBefore INTEGER, CharsAfter INTEGER, ResponseChars INTEGER, ElapsedMs INTEGER);
            CREATE INDEX IX_ProxyExchanges_SessionId ON ProxyExchanges(SessionId);
            """;
        command.ExecuteNonQuery();
    }

    private void Insert(string id, string? session, string before, string after, string response, bool truncated)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ProxyExchanges VALUES ($id,$session,'2026-09-16T00:00:00Z','openai','POST','/v1/responses',200,
                $before,$after,$response,$truncated,$beforeChars,$afterChars,$responseChars,123);
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$session", (object?)session ?? DBNull.Value);
        command.Parameters.AddWithValue("$before", before);
        command.Parameters.AddWithValue("$after", after);
        command.Parameters.AddWithValue("$response", response);
        command.Parameters.AddWithValue("$truncated", truncated);
        command.Parameters.AddWithValue("$beforeChars", before.Length);
        command.Parameters.AddWithValue("$afterChars", after.Length);
        command.Parameters.AddWithValue("$responseChars", response.Length);
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        // The archive reader uses pooling; release only its exact pool before removing this test file.
        using var pooled = SqliteConnectionFactory.Create(new SqliteConnectionStringBuilder { DataSource = _path }.ToString(), readOnly: true);
        SqliteConnection.ClearPool(pooled);
        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" })
            File.Delete(file);
        foreach (var file in new[] { _path + ".state", _path + ".state-wal", _path + ".state-shm" })
            File.Delete(file);
    }
}
