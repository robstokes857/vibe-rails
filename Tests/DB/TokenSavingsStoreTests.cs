using Microsoft.Data.Sqlite;
using VibeRails.DB;
using Xunit;

namespace Tests.DB;

/// <summary>
/// Pins the token-savings tally's contract: per-day upsert-increments in state.db, running totals
/// that survive restarts, and — by explicit user decision — a write path that never throws and
/// never blocks the caller, even against an unusable database.
/// </summary>
public sealed class TokenSavingsStoreTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"viberails-tokensavings-test-{Guid.NewGuid():N}.db");

    private string ConnectionString => $"Data Source={_dbPath};Mode=ReadWriteCreate";

    [Fact]
    public async Task Record_SameDay_SumsIntoOneRow()
    {
        var store = new TokenSavingsStore(ConnectionString);

        store.Record("anthropic", 1000, 700);
        await store.LastPersist;
        store.Record("anthropic", 500, 500); // measured but not rewritten
        await store.LastPersist;

        var (requests, rewritten, before, after) = ReadDayRow("anthropic");
        Assert.Equal(2, requests);
        Assert.Equal(1, rewritten); // only the shrinking request counts as rewritten
        Assert.Equal(1500, before);
        Assert.Equal(1200, after);

        var totals = store.GetTotals();
        Assert.Equal(1500, totals.BytesBefore);
        Assert.Equal(1200, totals.BytesAfter);
        Assert.Equal(300, totals.BytesSaved);
        Assert.Equal(75, totals.TokensSaved); // bytes/4 display heuristic
    }

    [Fact]
    public async Task GetTotals_NewStoreOnExistingDb_IncludesPersistedHistory()
    {
        var first = new TokenSavingsStore(ConnectionString);
        first.Record("anthropic", 2000, 1000);
        await first.LastPersist;

        // Simulates a restart: a fresh store must fold persisted history into its totals exactly
        // once, even when new records land before the lazy load runs.
        var second = new TokenSavingsStore(ConnectionString);
        second.Record("anthropic", 100, 50);
        await second.LastPersist;

        var totals = second.GetTotals();
        Assert.Equal(2100, totals.BytesBefore);
        Assert.Equal(1050, totals.BytesAfter);
    }

    [Fact]
    public async Task Record_UnusableDatabase_NeverThrowsAndKeepsInMemoryTotals()
    {
        var store = new TokenSavingsStore(
            $"Data Source={Path.Combine(_dbPath, "nested", "into-a-file.db")};Mode=ReadWrite");

        store.Record("anthropic", 400, 100);
        await store.LastPersist; // swallow-inside: the task completes instead of faulting
        store.Record("anthropic", 100, 100);
        await store.LastPersist;

        var totals = store.GetTotals();
        Assert.Equal(500, totals.BytesBefore);
        Assert.Equal(200, totals.BytesAfter);
    }

    [Fact]
    public async Task Record_ParallelWrites_AllLand()
    {
        var store = new TokenSavingsStore(ConnectionString);

        Parallel.For(0, 20, _ => store.Record("anthropic", 100, 60));
        await store.LastPersist;
        // LastPersist only tracks the newest write; the semaphore serializes them, so draining the
        // queue means waiting until the row shows all 20.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && ReadDayRow("anthropic").Requests < 20)
            await Task.Delay(50);

        var (requests, rewritten, before, after) = ReadDayRow("anthropic");
        Assert.Equal(20, requests);
        Assert.Equal(20, rewritten);
        Assert.Equal(2000, before);
        Assert.Equal(1200, after);
    }

    private (long Requests, long Rewritten, long Before, long After) ReadDayRow(string provider)
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT Requests, RewrittenRequests, BytesBefore, BytesAfter FROM TokenSavings WHERE Provider = $p";
        command.Parameters.AddWithValue("$p", provider);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3))
            : (0, 0, 0, 0);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            File.Delete(_dbPath);
        }
        catch (IOException)
        {
            // Best-effort temp cleanup; the OS temp dir owns the leftovers.
        }
    }
}
