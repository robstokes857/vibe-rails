using Microsoft.Data.Sqlite;
using TokenSaver;
using TokenSaver.Pipeline;
using VibeRails.DB;
using Xunit;

namespace Tests.DB;

/// <summary>
/// Pins the capture store's contract: rows round-trip verbatim (including the trace and the exact
/// stage selection that produced them), the list is newest-first and never carries the big strings,
/// and — by explicit user decision — the write path never throws and never blocks the caller, even
/// against an unusable database.
/// </summary>
public sealed class CompressionCaptureStoreTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"viberails-compressioncapture-test-{Guid.NewGuid():N}.db");
    private readonly List<CompressionCaptureStore> _stores = [];

    private string ConnectionString => $"Data Source={_dbPath};Mode=ReadWriteCreate";

    private CompressionCaptureStore NewStore(
        string? connectionString = null,
        int? queueCapacity = null,
        long? maxQueuedChars = null)
    {
        var store = queueCapacity.HasValue || maxQueuedChars.HasValue
            ? new CompressionCaptureStore(
                connectionString ?? ConnectionString,
                queueCapacity ?? CompressionCaptureStore.DefaultQueueCapacity,
                maxQueuedChars ?? CompressionCaptureStore.DefaultMaxQueuedChars)
            : new CompressionCaptureStore(connectionString ?? ConnectionString);
        _stores.Add(store);
        return store;
    }

    private static CompressionCapture NewCapture(
        string toolName = "Bash",
        string? command = "git status --short",
        string rawText = "a\na\na\n",
        string compressedText = "a [x3]\n",
        IReadOnlyList<StageTrace>? trace = null,
        IReadOnlyList<string>? enabledIds = null,
        bool rewriteAccepted = true) =>
        new(
            Guid.NewGuid(),
            "anthropic",
            toolName,
            command,
            rawText,
            compressedText,
            trace ?? [new StageTrace(CompressionCatalog.DedupeLines, StageOutcome.Applied, 4)],
            enabledIds ?? [CompressionCatalog.DedupeLines],
            rewriteAccepted);

    [Fact]
    public async Task Record_ThenGet_RoundTripsEveryField()
    {
        var store = NewStore();
        var capture = NewCapture(
            trace:
            [
                new StageTrace(CompressionCatalog.CrCollapse, StageOutcome.Disabled),
                new StageTrace(CompressionCatalog.GrepGroup, StageOutcome.NotApplicable),
                new StageTrace(CompressionCatalog.DedupeLines, StageOutcome.Applied, 4),
            ],
            enabledIds: [CompressionCatalog.DedupeLines, CompressionCatalog.BlankEdges]);

        store.Record(capture);
        await store.WaitForDrainAsync();

        var detail = await store.GetAsync(capture.Id, CancellationToken.None);
        Assert.NotNull(detail);
        Assert.Equal(capture.Id, detail.Id);
        Assert.Equal("anthropic", detail.Provider);
        Assert.Equal("Bash", detail.ToolName);
        Assert.Equal("git status --short", detail.Command);
        Assert.Equal("a\na\na\n", detail.RawText);
        Assert.Equal("a [x3]\n", detail.CompressedText);
        Assert.Equal(6, detail.CharsBefore);
        Assert.Equal(7, detail.CharsAfter);
        Assert.True(detail.Changed);
        Assert.True(detail.RewriteAccepted);
        Assert.Equal(DateTimeKind.Utc, detail.CreatedUtc.Kind);

        // Stage traces are deliberately NOT persisted (2026-07-28): they describe the pipeline on
        // the day they were written, and the pipeline changes with every compression change, so a
        // stored trace cannot be re-judged against today's stages. The raw before/after above is
        // stage-independent and is what a what-if actually replays. EnabledIds still survives —
        // that is configuration, not attribution, and a capture must state what it ran under.
        Assert.Empty(detail.Trace);
        Assert.Equal([CompressionCatalog.DedupeLines, CompressionCatalog.BlankEdges], detail.EnabledIds);
        Assert.True(Assert.Single(await store.ListAsync(10, 0, CancellationToken.None)).RewriteAccepted);
    }

    [Fact]
    public async Task Record_NullCommandAndUnchangedText_RoundTrips()
    {
        var store = NewStore();
        // A Read tool_result the pipeline declined to touch: no command to classify, nothing removed.
        var capture = NewCapture(
            toolName: "Read", command: null, rawText: "unchanged", compressedText: "unchanged",
            trace: [], enabledIds: [], rewriteAccepted: false);

        store.Record(capture);
        await store.WaitForDrainAsync();

        var detail = await store.GetAsync(capture.Id, CancellationToken.None);
        Assert.NotNull(detail);
        Assert.Null(detail.Command);
        Assert.False(detail.Changed);
        Assert.False(detail.RewriteAccepted);
        Assert.Empty(detail.Trace);
        Assert.Empty(detail.EnabledIds);
    }

    [Fact]
    public async Task Get_UnknownId_ReturnsNull()
    {
        var store = NewStore();
        Assert.Null(await store.GetAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task List_IsNewestFirstAndPages()
    {
        var now = new DateTime(2026, 7, 15, 12, 0, 0, DateTimeKind.Utc);
        var store = NewStore();
        store.UtcNow = () => now;

        var oldest = NewCapture(command: "oldest");
        store.Record(oldest);
        await store.WaitForDrainAsync();

        now = now.AddMinutes(1);
        var middle = NewCapture(command: "middle");
        store.Record(middle);
        await store.WaitForDrainAsync();

        now = now.AddMinutes(1);
        var newest = NewCapture(command: "newest");
        store.Record(newest);
        await store.WaitForDrainAsync();

        var page = await store.ListAsync(take: 10, skip: 0, CancellationToken.None);
        Assert.Equal([newest.Id, middle.Id, oldest.Id], page.Select(c => c.Id));

        var second = await store.ListAsync(take: 1, skip: 1, CancellationToken.None);
        Assert.Equal(middle.Id, Assert.Single(second).Id);
    }

    [Fact]
    public async Task List_SameTimestamp_StaysStableAcrossPages()
    {
        // DateTime.UtcNow's resolution on Windows (~15ms) is coarser than the write path is fast,
        // so identical CreatedUTC values are routine. Without the rowid tiebreak the pages would
        // overlap and drop rows.
        var now = new DateTime(2026, 7, 15, 12, 0, 0, DateTimeKind.Utc);
        var store = NewStore();
        store.UtcNow = () => now;

        var ids = new List<Guid>();
        for (var i = 0; i < 6; i++)
        {
            var capture = NewCapture(command: $"cmd-{i}");
            ids.Add(capture.Id);
            store.Record(capture);
            await store.WaitForDrainAsync();
        }

        var first = await store.ListAsync(take: 3, skip: 0, CancellationToken.None);
        var second = await store.ListAsync(take: 3, skip: 3, CancellationToken.None);

        // Insertion order reversed, with no row appearing on both pages.
        ids.Reverse();
        Assert.Equal(ids, first.Concat(second).Select(c => c.Id));
    }

    [Fact]
    public async Task List_DegenerateTakeAndSkip_AreClamped()
    {
        // take/skip arrive straight off the query string, so the store treats them as untrusted.
        var store = NewStore();
        // Distinct rawText per row: identical captures dedupe to one, which would leave this
        // asserting paging behaviour against a single row and prove nothing.
        store.Record(NewCapture(rawText: "one\n"));
        await store.WaitForDrainAsync();
        store.Record(NewCapture(rawText: "two\n"));
        await store.WaitForDrainAsync();

        // take 0 is LIMIT 0 — an empty page for a table that demonstrably has rows.
        Assert.Single(await store.ListAsync(take: 0, skip: 0, CancellationToken.None));
        Assert.Single(await store.ListAsync(take: -1, skip: 0, CancellationToken.None));

        // The table is uncapped, so an unbounded take is a request for all of history at once.
        // Both of these survive only because they are clamped before they reach SQLite.
        Assert.Equal(2, (await store.ListAsync(take: int.MaxValue, skip: 0, CancellationToken.None)).Count);
        Assert.Equal(2, (await store.ListAsync(take: 10, skip: -5, CancellationToken.None)).Count);
    }

    [Fact]
    public async Task ClearAsync_DeletesEverythingAndReturnsCount()
    {
        var store = NewStore();
        var kept = NewCapture(rawText: "one\n");
        store.Record(kept);
        await store.WaitForDrainAsync();
        store.Record(NewCapture(rawText: "two\n"));
        await store.WaitForDrainAsync();

        Assert.Equal(2, await store.ClearAsync(CancellationToken.None));
        Assert.Empty(await store.ListAsync(take: 10, skip: 0, CancellationToken.None));
        Assert.Null(await store.GetAsync(kept.Id, CancellationToken.None));

        // Clearing an already-empty table is a no-op, not an error: it is the reset button.
        Assert.Equal(0, await store.ClearAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ClearAsync_OrdersAfterQueuedWrites_AndAllowsSameCaptureAgain()
    {
        var store = NewStore();
        var capture = NewCapture(rawText: "queued\n");

        store.Record(capture);
        Assert.Equal(1, await store.ClearAsync(CancellationToken.None));
        Assert.Empty(await store.ListAsync(10, 0, CancellationToken.None));

        var repeated = capture with { Id = Guid.NewGuid() };
        store.Record(repeated);
        await store.WaitForDrainAsync();

        Assert.NotNull(await store.GetAsync(repeated.Id, CancellationToken.None));
    }

    [Fact]
    public async Task ClearFromAnotherStore_StaleSeenCacheFallsBackToUpsert()
    {
        // Root and terminal-child processes own separate stores over the same state.db. Clearing
        // from one cannot directly clear the other's in-memory dedupe cache.
        var clearingStore = NewStore();
        var childStore = NewStore();
        var original = NewCapture(rawText: "shared history\n");

        clearingStore.Record(original);
        await clearingStore.WaitForDrainAsync();

        // Populate the child store's _seen cache through the existing row.
        childStore.Record(original with { Id = Guid.NewGuid() });
        await childStore.WaitForDrainAsync();

        Assert.Equal(1, await clearingStore.ClearAsync(CancellationToken.None));

        var afterClear = original with { Id = Guid.NewGuid() };
        childStore.Record(afterClear);
        await childStore.WaitForDrainAsync();

        Assert.NotNull(await childStore.GetAsync(afterClear.Id, CancellationToken.None));
        Assert.Equal(1, ReadRowCount());
    }

    [Fact]
    public async Task ExistingPreDedupeSchema_IsMigratedBeforeHashIndexCreation()
    {
        using (var connection = new SqliteConnection(ConnectionString))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE CompressionCaptures (
                    Id TEXT PRIMARY KEY, CreatedUTC TEXT NOT NULL, Provider TEXT NOT NULL,
                    ToolName TEXT NOT NULL, Command TEXT, RawText TEXT NOT NULL,
                    CompressedText TEXT NOT NULL, CharsBefore INTEGER NOT NULL,
                    CharsAfter INTEGER NOT NULL, Changed INTEGER NOT NULL,
                    EnabledIds TEXT NOT NULL, Trace TEXT NOT NULL
                );
                """;
            command.ExecuteNonQuery();
        }

        var store = NewStore();
        var capture = NewCapture();
        store.Record(capture);
        await store.WaitForDrainAsync();

        Assert.NotNull(await store.GetAsync(capture.Id, CancellationToken.None));
    }

    [Fact]
    public async Task ListAndGet_FreshDatabase_ReturnEmptyRatherThanThrow()
    {
        // Readers can run before any Repository has initialized the schema.
        var store = NewStore();
        Assert.Empty(await store.ListAsync(take: 10, skip: 0, CancellationToken.None));
        Assert.Null(await store.GetAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task Record_UnusableDatabase_NeverThrows()
    {
        var store = NewStore($"Data Source={Path.Combine(_dbPath, "nested", "into-a-file.db")};Mode=ReadWrite");

        store.Record(NewCapture());
        await store.WaitForDrainAsync(); // swallow-inside: the task completes instead of faulting
        store.Record(NewCapture());
        await store.WaitForDrainAsync();
    }

    [Fact]
    public async Task Record_OverMemoryBudget_DropsImmediately()
    {
        var store = NewStore(queueCapacity: 1, maxQueuedChars: 4);

        store.Record(NewCapture(rawText: "too large", compressedText: "x"));
        await store.WaitForDrainAsync();

        Assert.Equal(1, store.FailedWrites);
        Assert.Empty(await store.ListAsync(10, 0, CancellationToken.None));
    }

    [Fact]
    public async Task Record_ParallelDistinctWrites_AllLand()
    {
        var store = NewStore();

        Parallel.For(0, 20, i => store.Record(NewCapture(rawText: $"output {i}\n")));
        await store.WaitForDrainAsync();
        Assert.Equal(20, ReadRowCount());
    }

    [Fact]
    public async Task Record_IdenticalCaptures_CollapseToOneRowAndCountSightings()
    {
        // The reason dedupe exists: the Messages API is stateless, so the CLI re-sends its whole
        // history every turn and the proxy re-compresses — and would re-capture — every tool_result
        // in it. Row count would be triangular in turn count with ~96% byte-identical duplicates.
        var store = NewStore();

        for (var turn = 0; turn < 20; turn++)
            store.Record(NewCapture(rawText: "npm warn deprecated\n"));

        await store.WaitForDrainAsync();

        Assert.Equal(1, ReadRowCount());
        // The sightings are the information a duplicate row would have carried: this output cost the
        // context window 20 times over, which is the number that says where compression is worth it.
        Assert.Equal(20, ReadSeenCount());
    }

    [Fact]
    public async Task Record_SameTextDifferentStageSelection_AreSeparateCaptures()
    {
        // EnabledIds is part of the dedupe key: the same output under a different stage set is a
        // different experiment, and collapsing the two would make the what-if preview unjudgeable.
        var store = NewStore();

        store.Record(NewCapture(rawText: "same\n", enabledIds: [CompressionCatalog.DedupeLines]));
        await store.WaitForDrainAsync();
        store.Record(NewCapture(rawText: "same\n", enabledIds: [CompressionCatalog.AnsiStrip]));
        await store.WaitForDrainAsync();

        Assert.Equal(2, ReadRowCount());
    }

    [Fact]
    public async Task Record_SameStageSelectionInDifferentOrder_IsOneCapture()
    {
        // A selection is a set. A UI that reorders its checkboxes must not fork history.
        var store = NewStore();
        string[] forward = [CompressionCatalog.AnsiStrip, CompressionCatalog.DedupeLines];
        string[] reversed = [CompressionCatalog.DedupeLines, CompressionCatalog.AnsiStrip];

        store.Record(NewCapture(rawText: "same\n", enabledIds: forward));
        await store.WaitForDrainAsync();
        store.Record(NewCapture(rawText: "same\n", enabledIds: reversed));
        await store.WaitForDrainAsync();

        Assert.Equal(1, ReadRowCount());
    }

    [Fact]
    public async Task Record_SameInputAndSelectionButDifferentOutput_AreSeparateCaptures()
    {
        var store = NewStore();
        store.Record(NewCapture(rawText: "same\n", compressedText: "old\n"));
        store.Record(NewCapture(rawText: "same\n", compressedText: "new\n"));
        await store.WaitForDrainAsync();

        Assert.Equal(2, ReadRowCount());
    }

    private int ReadSeenCount()
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT SeenCount FROM CompressionCaptures LIMIT 1;";
        try
        {
            // The store creates the table on its first write, so a poll can arrive before it exists.
            return Convert.ToInt32(command.ExecuteScalar());
        }
        catch (SqliteException)
        {
            return 0;
        }
    }

    private int ReadRowCount()
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM CompressionCaptures";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    public void Dispose()
    {
        foreach (var store in _stores)
            store.Dispose();
        // Scoped to this class's connection string: a process-wide ClearAllPools() disposes handles
        // out from under DB test classes running in parallel.
        SqliteConnection.ClearPool(new SqliteConnection(ConnectionString));
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
