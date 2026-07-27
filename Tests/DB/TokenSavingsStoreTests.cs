using System.Diagnostics;
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
        // With no prior history, all three windows agree.
        Assert.Equal(75, totals.SessionTokensSaved);
        Assert.Equal(75, totals.MonthTokensSaved);
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
        // The session window covers only this process; both writes are (normally) this month.
        Assert.Equal(100, totals.SessionBytesBefore);
        Assert.Equal(50, totals.SessionBytesAfter);
        Assert.Equal(2100, totals.MonthBytesBefore);
        Assert.Equal(1050, totals.MonthBytesAfter);
    }

    [Fact]
    public async Task GetTotals_PriorMonthHistory_CountsAllTimeButNotThisMonth()
    {
        var priorMonthDay = DateTime.UtcNow.AddMonths(-1).ToString("yyyy-MM") + "-15";
        InsertDayRow(priorMonthDay, "anthropic", bytesBefore: 4000, bytesAfter: 3000);

        var store = new TokenSavingsStore(ConnectionString);
        store.Record("anthropic", 100, 60);
        await store.LastPersist;

        var totals = store.GetTotals();
        Assert.Equal(4100, totals.BytesBefore);
        Assert.Equal(3060, totals.BytesAfter);
        Assert.Equal(100, totals.MonthBytesBefore);
        Assert.Equal(60, totals.MonthBytesAfter);
        Assert.Equal(260, totals.TokensSaved);
        Assert.Equal(10, totals.SessionTokensSaved);
        Assert.Equal(10, totals.MonthTokensSaved);
    }

    [Fact]
    public void GetTotals_OnSynchronizationContext_DoesNotWaitForHistoryLoad()
    {
        var returned = false;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                // Task.Yield posts the constructor's warm-up continuation here, but this context
                // deliberately never pumps it. A sync-over-async GetTotals would deadlock.
                SynchronizationContext.SetSynchronizationContext(new NonPumpingSynchronizationContext());
                var store = new TokenSavingsStore(ConnectionString);

                var stopwatch = Stopwatch.StartNew();
                _ = store.GetTotals();
                stopwatch.Stop();

                returned = stopwatch.Elapsed < TimeSpan.FromSeconds(1);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        thread.IsBackground = true;
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(2)), "GetTotals blocked on background initialization.");
        Assert.Null(failure);
        Assert.True(returned);
    }

    [Fact]
    public async Task HistoryLoad_FailedAfterTotalsQuery_RetriesWithoutDoubleCounting()
    {
        // The all-time query can read this legacy/broken shape, but the following month query fails
        // because Day is missing. This pins that partial reads stay staged until the whole load wins.
        using (var connection = new SqliteConnection(ConnectionString))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE TokenSavings (BytesBefore INTEGER NOT NULL, BytesAfter INTEGER NOT NULL);
                INSERT INTO TokenSavings (BytesBefore, BytesAfter) VALUES (4000, 3000);
                """;
            command.ExecuteNonQuery();
        }

        var store = new TokenSavingsStore(ConnectionString);
        await store.LastPersist;
        Assert.False(store.HistoryLoaded);

        using (var connection = new SqliteConnection(ConnectionString))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE TokenSavings;";
            command.ExecuteNonQuery();
        }
        InsertDayRow(DateTime.UtcNow.ToString("yyyy-MM") + "-15", "anthropic", 4000, 3000);

        // Move the test clock beyond the failure backoff. GetTotals schedules the retry but returns
        // immediately; LastPersist lets the test observe that background attempt settling.
        store.UtcNow = () => DateTime.UtcNow.AddMinutes(1);
        _ = store.GetTotals();
        await store.LastPersist;

        Assert.True(store.HistoryLoaded);
        var totals = store.GetTotals();
        Assert.Equal(4000, totals.BytesBefore);
        Assert.Equal(3000, totals.BytesAfter);
        Assert.Equal(1000, totals.BytesSaved);
    }

    [Fact]
    public async Task GetTotals_MonthRollsOver_MonthResetsButSessionAndAllTimeKeepCounting()
    {
        var start = new DateTime(2026, 7, 31, 23, 0, 0, DateTimeKind.Utc);
        var now = start;
        var store = new TokenSavingsStore(ConnectionString) { UtcNow = () => now };

        store.Record("anthropic", 1000, 600);
        await store.LastPersist;
        Assert.Equal(100, store.GetTotals().MonthTokensSaved);

        // Cross into August with no traffic yet: the month window empties, the others hold.
        now = start.AddHours(2);
        var afterRollover = store.GetTotals();
        Assert.Equal(0, afterRollover.MonthTokensSaved);
        Assert.Equal(100, afterRollover.SessionTokensSaved);
        Assert.Equal(100, afterRollover.TokensSaved);

        store.Record("anthropic", 400, 200);
        await store.LastPersist;
        var augustTotals = store.GetTotals();
        Assert.Equal(50, augustTotals.MonthTokensSaved); // August's record only
        Assert.Equal(150, augustTotals.SessionTokensSaved);
        Assert.Equal(150, augustTotals.TokensSaved);
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
        // Session and month tallies are pure in-memory bookkeeping: still intact with no DB.
        Assert.Equal(500, totals.SessionBytesBefore);
        Assert.Equal(200, totals.SessionBytesAfter);
        Assert.Equal(500, totals.MonthBytesBefore);
        Assert.Equal(200, totals.MonthBytesAfter);
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
            await Task.Delay(50, TestContext.Current.CancellationToken);

        var (requests, rewritten, before, after) = ReadDayRow("anthropic");
        Assert.Equal(20, requests);
        Assert.Equal(20, rewritten);
        Assert.Equal(2000, before);
        Assert.Equal(1200, after);
    }

    private void InsertDayRow(string day, string provider, long bytesBefore, long bytesAfter)
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using (var create = connection.CreateCommand())
        {
            create.CommandText = SqlStrings.CreateTokenSavingsTable;
            create.ExecuteNonQuery();
        }

        using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO TokenSavings (Day, Provider, Requests, RewrittenRequests, BytesBefore, BytesAfter, UpdatedUTC)
            VALUES ($day, $provider, 1, 1, $bytesBefore, $bytesAfter, $updatedUTC)
            """;
        insert.Parameters.AddWithValue("$day", day);
        insert.Parameters.AddWithValue("$provider", provider);
        insert.Parameters.AddWithValue("$bytesBefore", bytesBefore);
        insert.Parameters.AddWithValue("$bytesAfter", bytesAfter);
        insert.Parameters.AddWithValue("$updatedUTC", DateTime.UtcNow.ToString("o"));
        insert.ExecuteNonQuery();
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

    private sealed class NonPumpingSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state)
        {
            // Deliberately do not run queued continuations.
        }
    }
}
