using System.Text.Json;
using Microsoft.Data.Sqlite;
using VibeRails.Data.Sqlite;
using VibeRails.Data.Sqlite.Replay;
using VibeRails.Services.SessionReplay;
using Xunit;

namespace Tests.DB;

public sealed class ReplayExchangeTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "replay-exchanges-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteConnection db;
    private readonly ReplayStore store;

    public ReplayExchangeTests()
    {
        Directory.CreateDirectory(directory);
        var proxy = Path.Combine(directory, "proxy.db");
        db = new SqliteConnection($"Data Source={proxy};Pooling=False");
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            CREATE TABLE ProxyExchanges(Id TEXT,SessionId TEXT,CreatedUTC TEXT,Provider TEXT,Method TEXT,Path TEXT,
                StatusCode INTEGER,ElapsedMs INTEGER,ResponseTruncated INTEGER,ResponseBody TEXT,RequestBefore TEXT,RequestAfter TEXT);
            """;
        command.ExecuteNonQuery();
        store = new ReplayStore(new SqliteStoragePaths(Path.Combine(directory, "state.db"), null, proxy));
    }

    private void Add(int id, string response, string session = "one")
    {
        using var command = db.CreateCommand();
        command.CommandText = """
            INSERT INTO ProxyExchanges VALUES($id,$session,'2026-09-30T00:00:00Z','codex','POST','/responses',200,10,0,$body,
                '{"model":"test-model","reasoning":{"effort":"high"}}','{}');
            """;
        command.Parameters.AddWithValue("$id", id.ToString());
        command.Parameters.AddWithValue("$session", session);
        command.Parameters.AddWithValue("$body", response);
        command.ExecuteNonQuery();
    }

    [Fact]
    public void MaximumCaptureIsClippedInSqlBeforeManagedMaterialization()
    {
        Add(1, "");
        using (var command = db.CreateCommand())
        {
            command.CommandText = """
                UPDATE ProxyExchanges SET ResponseBody =
                    '{"output":[{"type":"function_call","name":"large","arguments":"' ||
                    replace(hex(zeroblob(32 * 1024 * 1024)), '00', 'x') || '"}]}';
                """;
            command.ExecuteNonQuery();
        }
        // Warm up the normal store/parser path so this measures the oversized read itself.
        _ = ToolParser.Parse("{}");
        _ = store.Exchanges("missing", 0, 1);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var page = store.Exchanges("one", 0, 1);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 32 * 1024 * 1024, $"Summary read allocated {allocated:N0} bytes.");
        var exchange = Assert.Single(page.Items);
        Assert.Contains("truncated", exchange.ParseNote);
        Assert.False(exchange.Truncated); // This was a display bound, not capture truncation.
        Assert.Empty(exchange.Tools);
        Assert.True(page.Done);
        using var check = db.CreateCommand();
        check.CommandText = "SELECT length(ResponseBody) FROM ProxyExchanges;";
        Assert.True((long)check.ExecuteScalar()! > 32 * 1024 * 1024); // Original retained.
    }

    [Fact]
    public void SummaryPagesBoundEscapedJsonWithoutSkippingExchanges()
    {
        var body = JsonSerializer.Serialize(new { output = Enumerable.Range(0, 12).Select(i => new
        {
            type = "function_call", call_id = "tool-" + i, name = "read", arguments = new string('\u0001', 12000)
        }) });
        for (var i = 1; i <= 30; i++) Add(i, body);
        Add(31, body, "other-session");
        Add(32, body); // Beyond the pinned snapshot.
        var seen = new List<string>();
        long cursor = 0;
        var pages = 0;
        while (true)
        {
            var page = store.Exchanges("one", cursor, 30);
            Assert.NotEmpty(page.Items);
            Assert.True(JsonSerializer.SerializeToUtf8Bytes(page, ReplayJson.Default.ExchangePage).Length <= ReplayStore.MaxExchangePageBytes);
            Assert.All(page.Items, item => Assert.Contains("truncated", item.ParseNote));
            seen.AddRange(page.Items.Select(item => item.Id));
            Assert.True(page.Next > cursor);
            Assert.Equal(page.Items[^1].Cursor, page.Next);
            cursor = page.Next;
            pages++;
            if (page.Done) break;
            Assert.True(pages < 31);
        }
        Assert.True(pages > 1);
        Assert.Equal(Enumerable.Range(1, 30).Select(i => i.ToString()), seen);
    }

    [Fact]
    public void MalformedCaptureOnlyAddsItsOwnNoteAndLaterCaptureStillLoads()
    {
        Add(1, """{"type":"content_block_start","content_block":{"type":"tool_use","name":"bad"}}""");
        Add(2, """{"output":[{"type":"function_call","call_id":"good","name":"read","arguments":"ok"}]}""");
        var page = store.Exchanges("one", 0, 2);
        Assert.Equal(2, page.Items.Count);
        Assert.Contains("invalid structure", page.Items[0].ParseNote);
        Assert.Empty(page.Items[0].Tools);
        Assert.Equal("good", Assert.Single(page.Items[1].Tools).Id);
        Assert.Null(page.Items[1].ParseNote);
        Assert.Equal("test-model", page.Items[1].Model);
        Assert.Equal("high", page.Items[1].Effort);
        Assert.True(page.Done);
    }

    public void Dispose()
    {
        db.Dispose();
        Directory.Delete(directory, recursive: true);
    }
}
