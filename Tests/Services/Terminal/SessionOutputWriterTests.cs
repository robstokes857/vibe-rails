using Microsoft.Data.Sqlite;
using Moq;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services.Terminal;
using Xunit;

namespace Tests.Services.Terminal;

/// <summary>
/// The writer persists a drain's worth of output in ONE repository call. These tests pin the
/// contract that replaced two autocommit INSERTs per PTY chunk: arrival order, replay-frame
/// splitting, retry on a lost writer lock, drop-and-continue on anything else, flush on dispose.
/// </summary>
public sealed class SessionOutputWriterTests
{
    private const string SessionId = "7b2f3c1e-0000-4000-8000-000000000001";

    private static (Mock<IRepository> Repository, List<IReadOnlyList<TerminalOutputWrite>> Batches) CreateRepository(
        Func<int, Exception?>? failureForCall = null)
    {
        var batches = new List<IReadOnlyList<TerminalOutputWrite>>();
        var calls = 0;
        var repository = new Mock<IRepository>(MockBehavior.Strict);
        repository
            .Setup(x => x.PersistTerminalOutputAsync(SessionId, It.IsAny<IReadOnlyList<TerminalOutputWrite>>(), It.IsAny<CancellationToken>()))
            .Returns<string, IReadOnlyList<TerminalOutputWrite>, CancellationToken>((_, rows, _) =>
            {
                var call = Interlocked.Increment(ref calls);
                var failure = failureForCall?.Invoke(call);
                if (failure is not null)
                    return Task.FromException(failure);
                lock (batches)
                    batches.Add(rows.ToList());
                return Task.CompletedTask;
            });
        return (repository, batches);
    }

    private static List<TerminalOutputWrite> Rows(List<IReadOnlyList<TerminalOutputWrite>> batches)
    {
        lock (batches)
            return batches.SelectMany(b => b).ToList();
    }

    [Fact]
    public async Task ScriptLines_KeepTheirTimestampAndStderrFlag_InBatchedReplayFrames()
    {
        var (repository, batches) = CreateRepository();
        var writer = new SessionOutputWriter(repository.Object);
        writer.Initialize(SessionId);
        var firstAt = new DateTime(2026, 9, 23, 0, 0, 0, DateTimeKind.Utc);
        writer.EnqueueLine("output\r\n"u8.ToArray(), false, firstAt);
        writer.EnqueueLine("error\r\n"u8.ToArray(), true, firstAt.AddSeconds(2));
        await writer.DisposeAsync();

        Assert.Single(batches);
        var rows = Rows(batches);
        Assert.Equal([false, true], rows.Where(row => row.Kind == TerminalOutputKind.Legacy).Select(row => row.IsError));
        var frames = rows.Where(row => row.Kind == TerminalOutputKind.Enriched).ToList();
        Assert.Equal([0, 1], frames.Select(row => row.Sequence));
        Assert.Equal([firstAt, firstAt.AddSeconds(2)], frames.Select(row => row.TimestampUtc));
        Assert.Equal(["output\r\n", "error\r\n"], frames.Select(row => System.Text.Encoding.UTF8.GetString(row.Data)));
    }

    [Fact]
    public async Task ChunksInOneBurstArePersistedInOneBatchInArrivalOrder()
    {
        var (repository, batches) = CreateRepository();
        var writer = new SessionOutputWriter(repository.Object);
        writer.Initialize(SessionId, 120, 30);

        writer.Enqueue("one"u8.ToArray());
        writer.Enqueue("two"u8.ToArray());
        writer.Enqueue("three"u8.ToArray());
        await writer.DisposeAsync();

        var rows = Rows(batches);
        var legacy = rows.Where(r => r.Kind == TerminalOutputKind.Legacy).Select(r => System.Text.Encoding.UTF8.GetString(r.Data)).ToList();
        Assert.Equal(new[] { "one", "two", "three" }, legacy);

        // No alt-screen transitions and under the flush threshold: the three chunks become one
        // replay frame at shutdown, after the legacy rows.
        var enriched = rows.Where(r => r.Kind == TerminalOutputKind.Enriched).ToList();
        var frame = Assert.Single(enriched);
        Assert.Equal("onetwothree", System.Text.Encoding.UTF8.GetString(frame.Data));
        Assert.Equal(0, frame.Sequence);
        Assert.False(frame.IsAlternateScreen);
        Assert.Equal((120, 30), (frame.Cols, frame.Rows));
        Assert.Equal(rows.Count - 1, rows.IndexOf(frame));
    }

    [Fact]
    public async Task AlternateScreenTransitionsSplitReplayFramesWithinTheBatch()
    {
        var (repository, batches) = CreateRepository();
        var writer = new SessionOutputWriter(repository.Object);
        writer.Initialize(SessionId, 100, 40);

        writer.Enqueue("before"u8.ToArray());
        writer.Enqueue("\x1b[?1049hinside\x1b[?1049l"u8.ToArray());
        writer.Enqueue("after"u8.ToArray());
        await writer.DisposeAsync();

        var enriched = Rows(batches).Where(r => r.Kind == TerminalOutputKind.Enriched).ToList();
        Assert.Equal(3, enriched.Count);
        Assert.Equal(new[] { 0, 1, 2 }, enriched.Select(r => r.Sequence));
        Assert.Equal("before", System.Text.Encoding.UTF8.GetString(enriched[0].Data));
        Assert.False(enriched[0].IsAlternateScreen);
        Assert.Equal("\x1b[?1049hinside\x1b[?1049l", System.Text.Encoding.UTF8.GetString(enriched[1].Data));
        Assert.True(enriched[1].IsAlternateScreen);
        Assert.Equal("after", System.Text.Encoding.UTF8.GetString(enriched[2].Data));
        Assert.False(enriched[2].IsAlternateScreen);
    }

    [Fact]
    public async Task ResizeFlushesAtTheOldGeometryAndRecordsAMarkerAtTheNew()
    {
        var (repository, batches) = CreateRepository();
        var writer = new SessionOutputWriter(repository.Object);
        writer.Initialize(SessionId, 80, 24);

        writer.Enqueue("small"u8.ToArray());
        writer.NotifyResize(200, 50);
        writer.NotifyResize(200, 50); // no-op: unchanged geometry must not add a marker
        writer.Enqueue("large"u8.ToArray());
        await writer.DisposeAsync();

        var enriched = Rows(batches).Where(r => r.Kind == TerminalOutputKind.Enriched).ToList();
        Assert.Equal(3, enriched.Count);
        Assert.Equal(("small", 80, 24), (System.Text.Encoding.UTF8.GetString(enriched[0].Data), enriched[0].Cols, enriched[0].Rows));
        Assert.Equal((0, 200, 50), (enriched[1].Data.Length, enriched[1].Cols, enriched[1].Rows));
        Assert.Equal(("large", 200, 50), (System.Text.Encoding.UTF8.GetString(enriched[2].Data), enriched[2].Cols, enriched[2].Rows));
        Assert.Equal(new[] { 0, 1, 2 }, enriched.Select(r => r.Sequence));
    }

    [Fact]
    public async Task ALostWriterLockIsRetriedAndTheBatchKeepsItsRowsAndSequenceNumbers()
    {
        // First attempt: SQLITE_BUSY. Second: succeeds. Nothing is lost and no number is burned.
        var (repository, batches) = CreateRepository(call => call == 1
            ? new SqliteException("SQLite Error 5: 'database is locked'.", 5)
            : null);
        var writer = new SessionOutputWriter(repository.Object);
        writer.Initialize(SessionId, 120, 30);

        writer.Enqueue("\x1b[?1049hframe\x1b[?1049l"u8.ToArray());
        writer.Enqueue("tail"u8.ToArray());
        await writer.DisposeAsync();

        var rows = Rows(batches);
        Assert.Equal(2, rows.Count(r => r.Kind == TerminalOutputKind.Legacy));
        var enriched = rows.Where(r => r.Kind == TerminalOutputKind.Enriched).ToList();
        Assert.Equal(new[] { 0, 1 }, enriched.Select(r => r.Sequence));
        repository.Verify(x => x.PersistTerminalOutputAsync(SessionId, It.IsAny<IReadOnlyList<TerminalOutputWrite>>(), It.IsAny<CancellationToken>()),
            Times.AtLeast(2));
    }

    [Fact]
    public async Task ANonTransientFailureDropsTheBatchAndTheWriterKeepsGoing()
    {
        var (repository, batches) = CreateRepository(call => call == 1
            ? new InvalidOperationException("disk gone")
            : null);
        var writer = new SessionOutputWriter(repository.Object);
        writer.Initialize(SessionId, 120, 30);

        writer.Enqueue("lost"u8.ToArray());
        // Give the first drain time to fail before the next chunk arrives in its own batch.
        await Task.Delay(SessionOutputWriter.CoalesceWindow * 6, TestContext.Current.CancellationToken);
        writer.Enqueue("kept"u8.ToArray());
        await writer.DisposeAsync();

        var legacy = Rows(batches).Where(r => r.Kind == TerminalOutputKind.Legacy)
            .Select(r => System.Text.Encoding.UTF8.GetString(r.Data)).ToList();
        Assert.Equal(new[] { "kept" }, legacy);
        // The dropped batch is not retried: exactly one failing call, then the survivors.
        repository.Verify(x => x.PersistTerminalOutputAsync(SessionId, It.IsAny<IReadOnlyList<TerminalOutputWrite>>(), It.IsAny<CancellationToken>()),
            Times.AtLeast(2));
    }

    [Fact]
    public async Task ATransientLockThatNeverClearsDropsAfterTheRetryBudget()
    {
        var attempts = 0;
        var (repository, batches) = CreateRepository(_ =>
        {
            Interlocked.Increment(ref attempts);
            return new SqliteException("SQLite Error 5: 'database is locked'.", 5);
        });
        var writer = new SessionOutputWriter(repository.Object);
        writer.Initialize(SessionId, 120, 30);

        writer.Enqueue("gone"u8.ToArray());
        await writer.DisposeAsync();

        Assert.Empty(Rows(batches));
        // One initial attempt per drain plus the retry budget. Two drains happen here: the chunk
        // and the shutdown flush of the replay frame -- both exhaust the same budget.
        Assert.Equal(2 * (SessionOutputWriter.RetryDelays.Length + 1), attempts);
    }
}
