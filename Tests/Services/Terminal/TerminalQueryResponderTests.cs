using System.Collections.Concurrent;
using System.Text;
using VibeRails.Services.Terminal;
using VibeRails.Services.Terminal.Consumers;
using Xunit;
using Emulator = TerminalEmulator.Terminal;

namespace Tests.Services.Terminal;

/// <summary>
/// VIBE-354: what the server tells a pass-through console host, and that it tells it once, in
/// order, without blocking the read loop.
/// </summary>
public sealed class TerminalQueryResponderTests
{
    [Theory]
    [InlineData(nameof(TerminalQuery.PrimaryDeviceAttributes), "\u001b[?1;2c")]
    [InlineData(nameof(TerminalQuery.SecondaryDeviceAttributes), "\u001b[>0;276;0c")]
    [InlineData(nameof(TerminalQuery.OperatingStatus), "\u001b[0n")]
    [InlineData(nameof(TerminalQuery.CursorPosition), "\u001b[5;9R")]
    [InlineData(nameof(TerminalQuery.ExtendedCursorPosition), "\u001b[?5;9R")]
    public void RepliesAreWhatXtermJsWouldSay(string query, string expected)
    {
        var reply = TerminalQueryResponder.BuildReply(
            Enum.Parse<TerminalQuery>(query), parameter: 0, new TerminalQueryState(4, 8, 30, 100));
        Assert.Equal(expected, Encoding.ASCII.GetString(reply!));
    }

    [Fact]
    public void NoReplyForNoQuery()
    {
        Assert.Null(TerminalQueryResponder.BuildReply(TerminalQuery.None, 0, new TerminalQueryState(0, 0, 30, 100)));
    }

    [Theory]
    [InlineData(nameof(TerminalQuery.PrivateMode), 2026, TerminalModeReport.Reset, "\u001b[?2026;2$y")]
    [InlineData(nameof(TerminalQuery.PrivateMode), 1049, TerminalModeReport.Set, "\u001b[?1049;1$y")]
    [InlineData(nameof(TerminalQuery.PrivateMode), 2027, TerminalModeReport.NotRecognized, "\u001b[?2027;0$y")]
    [InlineData(nameof(TerminalQuery.AnsiMode), 2, TerminalModeReport.PermanentlyReset, "\u001b[2;4$y")]
    public void AModeReportEchoesTheModeAndItsState(string query, int mode, int status, string expected)
    {
        var reply = TerminalQueryResponder.BuildReply(
            Enum.Parse<TerminalQuery>(query), mode, new TerminalQueryState(0, 0, 30, 100, status));
        Assert.Equal(expected, Encoding.ASCII.GetString(reply!));
    }

    [Fact]
    public void ModeStateComesFromTheEmulatorAndUnknownModesAreNotRecognized()
    {
        var emulator = new Emulator(cols: 40, rows: 10);
        int State(int mode) => TerminalModeReport.PrivateMode(emulator, mode);

        // A fresh terminal: what xterm.js 6 reports for the same modes (UITests xterm-device-queries.spec.js).
        Assert.Equal(TerminalModeReport.Reset, State(1049));
        Assert.Equal(TerminalModeReport.Reset, State(2004));
        Assert.Equal(TerminalModeReport.Reset, State(2026));
        Assert.Equal(TerminalModeReport.Reset, State(1000));
        Assert.Equal(TerminalModeReport.Reset, State(1004));
        Assert.Equal(TerminalModeReport.Reset, State(1006));
        Assert.Equal(TerminalModeReport.PermanentlySet, State(8));
        Assert.Equal(TerminalModeReport.PermanentlyReset, State(1005));
        Assert.Equal(TerminalModeReport.Set, State(1048));

        emulator.Write("\u001b[?1049h\u001b[?2004h\u001b[?2026h\u001b[?1003h\u001b[?1006h\u001b[?1004h");
        Assert.Equal(TerminalModeReport.Set, State(1049));
        Assert.Equal(TerminalModeReport.Set, State(47));
        Assert.Equal(TerminalModeReport.Set, State(2004));
        Assert.Equal(TerminalModeReport.Set, State(2026));
        Assert.Equal(TerminalModeReport.Set, State(1003));
        Assert.Equal(TerminalModeReport.Reset, State(1000)); // Mouse protocols replace each other.
        Assert.Equal(TerminalModeReport.Set, State(1006));
        Assert.Equal(TerminalModeReport.Set, State(1004));

        // Cursor visibility is withheld: the emulator calls a fresh cursor hidden, and a program
        // told so could "restore" a hidden cursor when it exits.
        Assert.False(new Emulator(cols: 40, rows: 10).CursorVisible);
        Assert.Equal(TerminalModeReport.NotRecognized, State(25));
        // Not tracked here. "Not recognized" makes a program leave the feature alone.
        Assert.Equal(TerminalModeReport.NotRecognized, State(1));
        Assert.Equal(TerminalModeReport.NotRecognized, State(7));
        Assert.Equal(TerminalModeReport.NotRecognized, State(2027));
        Assert.Equal(TerminalModeReport.NotRecognized, TerminalModeReport.AnsiMode(4));
        Assert.Equal(TerminalModeReport.PermanentlySet, TerminalModeReport.AnsiMode(12));
    }

    [Theory]
    // The host adopts whatever position it is told, so a report is always inside the grid.
    [InlineData(29, 100, "\u001b[30;100R")]  // Cursor resting past the last column (pending wrap).
    [InlineData(40, 5, "\u001b[30;6R")]      // Cursor not yet clamped after a shrink.
    [InlineData(-1, -1, "\u001b[1;1R")]
    public void ACursorReportIsClampedToTheGrid(int row, int col, string expected)
    {
        var reply = TerminalQueryResponder.BuildReply(
            TerminalQuery.CursorPosition, 0, new TerminalQueryState(row, col, Rows: 30, Cols: 100));
        Assert.Equal(expected, Encoding.ASCII.GetString(reply!));
    }

    [Fact]
    public async Task RepliesAreWrittenOnceEachInTheOrderTheQueriesArrived()
    {
        var ct = TestContext.Current.CancellationToken;
        var written = new ConcurrentQueue<string>();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var responder = new TerminalQueryResponder((bytes, _) =>
        {
            written.Enqueue(Encoding.ASCII.GetString(bytes.Span));
            if (written.Count == 4) done.TrySetResult();
            return Task.CompletedTask;
        }, ct);

        responder.Answer(TerminalQuery.PrimaryDeviceAttributes, 0, new TerminalQueryState(0, 0, 30, 100));
        responder.Answer(TerminalQuery.CursorPosition, 6, new TerminalQueryState(2, 3, 30, 100));
        responder.Answer(TerminalQuery.OperatingStatus, 5, new TerminalQueryState(0, 0, 30, 100));
        responder.Answer(TerminalQuery.CursorPosition, 6, new TerminalQueryState(7, 1, 30, 100));

        await done.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        await WaitUntilAsync(() => responder.AnsweredCount == 4, ct);
        Assert.Equal(["\u001b[?1;2c", "\u001b[3;4R", "\u001b[0n", "\u001b[8;2R"], written);
        Assert.Equal(0, responder.DroppedCount);
    }

    [Fact]
    public async Task AnsweringNeverWaitsForTheHostToReadInput()
    {
        // The read loop calls Answer. If the host is blocked writing output while its input pipe is
        // full, a write on that thread would deadlock the terminal.
        var ct = TestContext.Current.CancellationToken;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = 0;
        var responder = new TerminalQueryResponder(async (_, token) =>
        {
            Interlocked.Increment(ref writes);
            await release.Task.WaitAsync(token);
        }, ct);

        var answering = Task.Run(() =>
        {
            for (var i = 0; i < TerminalQueryResponder.MaxPendingReplies + 50; i++)
                responder.Answer(TerminalQuery.CursorPosition, 6, new TerminalQueryState(0, 0, 30, 100));
        }, ct);

        await answering.WaitAsync(TimeSpan.FromSeconds(10), ct);
        // One reply is held inside the blocked write; the queue behind it is capped.
        Assert.True(responder.DroppedCount > 0, "a flood of queries must not queue without bound");
        Assert.Equal(0, responder.AnsweredCount);

        release.SetResult();
        await WaitUntilAsync(() => responder.AnsweredCount >= TerminalQueryResponder.MaxPendingReplies, ct);
        Assert.True(Volatile.Read(ref writes) <= TerminalQueryResponder.MaxPendingReplies + 1);
    }

    [Fact]
    public async Task AFailedWriteStopsRepliesWithoutThrowingIntoTheReadLoop()
    {
        var ct = TestContext.Current.CancellationToken;
        var attempts = 0;
        var responder = new TerminalQueryResponder((_, _) =>
        {
            Interlocked.Increment(ref attempts);
            throw new ObjectDisposedException("pty");
        }, ct);

        responder.Answer(TerminalQuery.PrimaryDeviceAttributes, 0, new TerminalQueryState(0, 0, 30, 100));
        await WaitUntilAsync(() => Volatile.Read(ref attempts) == 1, ct);
        await Task.Delay(50, ct);
        // The PTY input is gone. Later queries are dropped quietly rather than queued forever.
        for (var i = 0; i < 1000; i++)
            responder.Answer(TerminalQuery.CursorPosition, 6, new TerminalQueryState(0, 0, 30, 100));
        await Task.Delay(50, ct);

        Assert.Equal(1, Volatile.Read(ref attempts));
        Assert.Equal(0, responder.AnsweredCount);
    }

    [Fact]
    public async Task ACursorReportDescribesThePointInTheStreamWhereTheProgramAsked()
    {
        // One chunk: text, the query, more text. The answer is where the cursor is after "abc",
        // not after the whole chunk.
        var (emulator, replies, consumer) = Create(cols: 40, rows: 10);
        consumer.OnOutput(Encoding.ASCII.GetBytes("\u001b[3;5Habc\u001b[6n\r\n\r\ntail"));

        Assert.Equal(["\u001b[3;8R"], await replies(1));
        // The rest of the chunk still reached the emulator: two line feeds below row 3, then "tail".
        Assert.Equal((4, 4), (emulator.CursorRow, emulator.CursorCol));
    }

    [Fact]
    public async Task EveryQueryInAChunkIsAnsweredAtItsOwnPosition()
    {
        var (_, replies, consumer) = Create(cols: 40, rows: 10);
        consumer.OnOutput(Encoding.ASCII.GetBytes("\u001b[c\u001b[2;2H\u001b[6nxy\u001b[?6n\u001b[5n\u001b[>c"));

        Assert.Equal(
            ["\u001b[?1;2c", "\u001b[2;2R", "\u001b[?2;4R", "\u001b[0n", "\u001b[>0;276;0c"],
            await replies(5));
    }

    [Fact]
    public async Task AModeQueryIsAnsweredFromTheStateAtThatPointInTheStream()
    {
        // Asked before and after the program turns synchronized output on, in one chunk.
        var (_, replies, consumer) = Create(cols: 40, rows: 10);
        consumer.OnOutput(Encoding.ASCII.GetBytes("\u001b[?2026$p\u001b[?2026h\u001b[?2026$p\u001b[?2026l\u001b[?2027$p\u001b[c"));

        Assert.Equal(
            ["\u001b[?2026;2$y", "\u001b[?2026;1$y", "\u001b[?2027;0$y", "\u001b[?1;2c"],
            await replies(4));
    }

    [Fact]
    public async Task AQuerySplitAcrossTwoReadsIsAnsweredOnce()
    {
        var (_, replies, consumer) = Create(cols: 40, rows: 10);
        consumer.OnOutput(Encoding.ASCII.GetBytes("hello\u001b["));
        consumer.OnOutput(Encoding.ASCII.GetBytes("6"));
        consumer.OnOutput(Encoding.ASCII.GetBytes("n world"));

        Assert.Equal(["\u001b[1;6R"], await replies(1));
    }

    [Fact]
    public void FeedingTheEmulatorInPiecesLeavesTheSameScreenAsFeedingItWhole()
    {
        // The split exists for the answer. It must not change what the emulator, and so every
        // reconnect snapshot, holds.
        var output = Encoding.UTF8.GetBytes(
            "\u001b[2J\u001b[Hline one\r\n\u001b[6n\u001b[38;2;10;20;30mcolour ✳ wide 漢字\u001b[0m\u001b[c\r\n"
            + "\u001b[?1049h\u001b[5;5Halt\u001b[?6n\u001b[?1049l\u001b]0;title [6n\u0007tail\u001b[5n");

        var plain = new Emulator(cols: 60, rows: 12);
        new TerminalEmulatorConsumer(plain, new Lock()).OnOutput(output);

        var (answering, _, consumer) = Create(cols: 60, rows: 12);
        // One byte at a time is the worst case for both the scanner and the emulator's parser.
        foreach (var b in output)
            consumer.OnOutput(new[] { b });

        Assert.Equal(plain.GetScreenText(), answering.GetScreenText());
        Assert.Equal((plain.CursorRow, plain.CursorCol), (answering.CursorRow, answering.CursorCol));
        Assert.Equal(plain.IsAlternateScreen, answering.IsAlternateScreen);
    }

    private static (Emulator Emulator, Func<int, Task<string[]>> Replies, QueryAnsweringEmulatorConsumer Consumer) Create(int cols, int rows)
    {
        var ct = TestContext.Current.CancellationToken;
        var written = new ConcurrentQueue<string>();
        var responder = new TerminalQueryResponder((bytes, _) =>
        {
            written.Enqueue(Encoding.ASCII.GetString(bytes.Span));
            return Task.CompletedTask;
        }, ct);
        var emulator = new Emulator(cols, rows);
        var consumer = new QueryAnsweringEmulatorConsumer(emulator, new Lock(), responder);
        return (emulator, async expected =>
        {
            await WaitUntilAsync(() => written.Count >= expected, ct);
            await Task.Delay(30, ct); // Anything written beyond the expected count would show up here.
            return [.. written];
        }, consumer);
    }

    // The pass-through host repaints nothing after a resize. It asks where the cursor is, the
    // answer is this emulator's, and the viewer has to have its own cursor in the same cell or the
    // next thing the program draws lands somewhere else. The emulator never re-wraps: it clamps.
    // UITests/tests/xterm-device-queries.spec.js checks the same table against the real xterm.js
    // with the options a bundled-host tab applies ("after a resize the viewer has its cursor
    // where the server emulator has it"); keep the two in step.
    [Theory]
    [InlineData(188, 36, 5, 134, 145, 23)]
    [InlineData(188, 36, 5, 180, 145, 23)]
    [InlineData(40, 10, 10, 8, 40, 6)]
    [InlineData(40, 10, 3, 8, 60, 20)]
    [InlineData(40, 10, 10, 40, 20, 4)]
    [InlineData(80, 24, 24, 1, 80, 30)]
    public void TheEmulatorClampsItsCursorOnAResizeTheWayTheViewerDoes(
        int cols, int rows, int cursorRow, int cursorCol, int newCols, int newRows)
    {
        var emulator = new Emulator(cols, rows);
        emulator.Write($"\u001b[{cursorRow};{cursorCol}H");

        emulator.Resize(newCols, newRows);

        var reply = TerminalQueryResponder.BuildReply(TerminalQuery.CursorPosition, 0,
            new TerminalQueryState(emulator.CursorRow, emulator.CursorCol, emulator.Rows, emulator.Cols));
        Assert.Equal(
            $"\u001b[{Math.Min(cursorRow, newRows)};{Math.Min(cursorCol, newCols)}R",
            Encoding.ASCII.GetString(reply!));
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out waiting for terminal query replies");
            await Task.Delay(5, ct);
        }
    }
}
