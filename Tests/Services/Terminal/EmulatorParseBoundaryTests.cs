using System.Text;
using VibeRails.Services.Terminal;
using VibeRails.Services.Terminal.Consumers;
using Xunit;
using Emulator = TerminalEmulator.Terminal;
using TerminalPty = VibeRails.Services.Terminal.Terminal;

namespace Tests.Services.Terminal;

/// <summary>
/// VIBE-354: a PTY read can end in the middle of an escape sequence, and a resize can arrive
/// before the next read. Scrolling the emulator through its input at that moment would end the
/// sequence in progress; its second half would be read as text and the cursor the console host
/// is told about would be wrong. <see cref="EmulatorParseBoundary"/> says when it is safe.
/// </summary>
public sealed class EmulatorParseBoundaryTests
{
    private const string Esc = "\u001b";
    private const string Bel = "\u0007";

    [Theory]
    [InlineData("", true)]
    [InlineData("plain text\r\n", true)]
    [InlineData(Esc, false)]
    [InlineData(Esc + "[", false)]
    [InlineData(Esc + "[31", false)]
    [InlineData(Esc + "[31;", false)]
    [InlineData(Esc + "[31m", true)]
    [InlineData(Esc + "[?", false)]
    [InlineData(Esc + "[?25h", true)]
    [InlineData(Esc + "[4 ", false)]
    [InlineData(Esc + "[4 q", true)]
    [InlineData(Esc + "]0;a window title", false)]
    [InlineData(Esc + "]0;a window title" + Bel, true)]
    [InlineData(Esc + "]0;a window title" + Esc, false)]
    [InlineData(Esc + "]0;a window title" + Esc + "\\", true)]
    [InlineData(Esc + "P$q", false)]
    [InlineData(Esc + "P$qm" + Esc + "\\", true)]
    [InlineData(Esc + "(", false)]
    [InlineData(Esc + "(B", true)]
    [InlineData(Esc + "7", true)]
    [InlineData(Esc + "[3\r\n", false)] // a control inside a sequence acts and leaves it open
    [InlineData(Esc + "[1;?", true)]    // a private marker anywhere but first aborts the sequence
    public void ItKnowsWhetherTheStreamEndsBetweenTwoSequences(string output, bool atBoundary)
    {
        var boundary = default(EmulatorParseBoundary);
        boundary.Advance(Encoding.UTF8.GetBytes(output));
        Assert.Equal(atBoundary, boundary.IsAtBoundary);
    }

    [Fact]
    public void AHalfReadCharacterIsNotABoundaryAndSurvivesASequenceInBetween()
    {
        byte[] star = [0xE2, 0x9C, 0xB3]; // U+2733, three bytes
        var boundary = default(EmulatorParseBoundary);

        boundary.Advance(star.AsSpan(0, 1));
        Assert.False(boundary.IsAtBoundary);
        boundary.Advance(star.AsSpan(1, 1));
        Assert.False(boundary.IsAtBoundary);
        // The emulator keeps the half character across a complete sequence; so does this.
        boundary.Advance(Encoding.ASCII.GetBytes(Esc + "[0m"));
        Assert.False(boundary.IsAtBoundary);
        boundary.Advance(star.AsSpan(2, 1));
        Assert.True(boundary.IsAtBoundary);

        byte[] emoji = [0xF0, 0x9F, 0x98, 0x80]; // four bytes
        for (var read = 1; read < emoji.Length; read++)
        {
            boundary.Advance(emoji.AsSpan(read - 1, 1));
            Assert.False(boundary.IsAtBoundary);
        }

        boundary.Advance(emoji.AsSpan(3, 1));
        Assert.True(boundary.IsAtBoundary);
    }

    // ---- Against the emulator itself ----------------------------------------------------------
    // Every prefix of these streams is a place a read can end. Where the tracker says "between
    // two sequences", writing a harmless complete sequence to the emulator there must leave the
    // final screen and cursor exactly as an uninterrupted feed leaves them. Where it says "inside
    // one", the same write must be able to change the outcome for at least one prefix, or the
    // tracker would be refusing for nothing.

    public static TheoryData<string> Streams() => new()
    {
        "prompt>" + Esc + "[31mX" + Esc + "[0m done",
        Esc + "]0;a window title" + Bel + "after" + Esc + "]2;second" + Esc + "\\ end",
        "a" + Esc + "(B" + "b" + Esc + "[?25l" + Esc + "[2;5H" + "c" + Esc + "7" + Esc + "8" + "d",
        "café ✳ \U0001F600 ok" + Esc + "[1;4;38;5;196mZ",
        Esc + "P$qm" + Esc + "\\" + "x" + Esc + "[4 q" + "y\r\n" + Esc + "[K" + "z",
    };

    [Theory]
    [MemberData(nameof(Streams))]
    public void WritingToTheEmulatorAtABoundaryChangesNothingAndInsideASequenceItCan(string stream)
    {
        var bytes = Encoding.UTF8.GetBytes(stream);
        var expected = Feed(bytes, splitAt: bytes.Length, interrupt: false);
        var refusedPrefixes = 0;
        var refusalMattered = false;

        for (var splitAt = 0; splitAt <= bytes.Length; splitAt++)
        {
            var boundary = default(EmulatorParseBoundary);
            boundary.Advance(bytes.AsSpan(0, splitAt));
            var interrupted = Feed(bytes, splitAt, interrupt: true);

            if (boundary.IsAtBoundary)
            {
                Assert.True(expected == interrupted,
                    $"a write at byte {splitAt}, reported as a boundary, changed the result:\n{expected}\n--- became ---\n{interrupted}");
            }
            else
            {
                refusedPrefixes++;
                refusalMattered |= expected != interrupted;
            }
        }

        Assert.True(refusedPrefixes > 0, "this stream has no place inside a sequence");
        Assert.True(refusalMattered, "no refused write would have changed anything: the tracker is too cautious");
    }

    [Fact]
    public void AResizeBetweenTheHalvesOfASequenceLeavesTheSequenceWhole()
    {
        // The case from review: nine lines and a prompt at 40x10, a read that ends at ESC [, a
        // shrink to 40x6, then the rest of the colour sequence and a character. xterm.js shows
        // prompt>X with its cursor in column 9; before the fix the emulator showed prompt>31mX.
        var (emulator, consumer) = NineLinesAndAPrompt();
        consumer.OnOutput(Encoding.ASCII.GetBytes(Esc + "["));
        Assert.False(consumer.IsAtSequenceBoundary);

        TerminalPty.ResizeEmulatorWithoutRepaint(emulator, consumer.IsAtSequenceBoundary, 40, 6);
        consumer.OnOutput(Encoding.ASCII.GetBytes("31mX"));

        Assert.DoesNotContain(emulator.GetScreenText(), line => line.Contains("31m", StringComparison.Ordinal));
        Assert.Equal((5, 8), (emulator.CursorRow, emulator.CursorCol));
        Assert.Equal('X', emulator.GetRowText(5)[7]);
        Assert.True(consumer.IsAtSequenceBoundary);
    }

    [Fact]
    public void AResizeInsideAWindowTitleDoesNotSpillTheTitleOntoTheScreen()
    {
        var (emulator, consumer) = NineLinesAndAPrompt();
        consumer.OnOutput(Encoding.ASCII.GetBytes(Esc + "]0;secret ti"));

        TerminalPty.ResizeEmulatorWithoutRepaint(emulator, consumer.IsAtSequenceBoundary, 40, 6);
        consumer.OnOutput(Encoding.ASCII.GetBytes("tle" + Bel + "Y"));

        var screen = string.Join('\n', emulator.GetScreenText());
        Assert.DoesNotContain("tle", screen, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", screen, StringComparison.Ordinal);
        Assert.Equal((5, 8), (emulator.CursorRow, emulator.CursorCol));
    }

    [Fact]
    public void AResizeBetweenSequencesStillKeepsTheLineTheCursorIsOn()
    {
        var (emulator, consumer) = NineLinesAndAPrompt();
        Assert.True(consumer.IsAtSequenceBoundary);

        TerminalPty.ResizeEmulatorWithoutRepaint(emulator, consumer.IsAtSequenceBoundary, 40, 6);

        Assert.Equal(["line 5", "line 6", "line 7", "line 8", "line 9", "prompt>"], emulator.GetScreenText());
        Assert.Equal((5, 7), (emulator.CursorRow, emulator.CursorCol));
    }

    private static (Emulator Emulator, QueryAnsweringEmulatorConsumer Consumer) NineLinesAndAPrompt()
    {
        var emulator = new Emulator(cols: 40, rows: 10);
        var responder = new TerminalQueryResponder((_, _) => Task.CompletedTask, CancellationToken.None);
        var consumer = new QueryAnsweringEmulatorConsumer(emulator, new Lock(), responder);
        var lines = new StringBuilder();
        for (var line = 1; line <= 9; line++) lines.Append($"line {line}\r\n");
        consumer.OnOutput(Encoding.ASCII.GetBytes(lines.Append("prompt>").ToString()));
        return (emulator, consumer);
    }

    // Feeds the stream in two reads and, optionally, writes a complete sequence in between that
    // changes nothing by itself (reset the margins that are already the whole screen, then put
    // the cursor back where it is), which is what the resize helper does.
    private static string Feed(byte[] bytes, int splitAt, bool interrupt)
    {
        var emulator = new Emulator(cols: 40, rows: 6);
        emulator.Write(bytes.AsSpan(0, splitAt));
        if (interrupt)
            emulator.Write($"{Esc}[r{Esc}[{emulator.CursorRow + 1};{emulator.CursorCol + 1}H");
        emulator.Write(bytes.AsSpan(splitAt));

        var cells = emulator.GetSnapshot();
        var screen = new StringBuilder();
        for (var row = 0; row < emulator.Rows; row++)
        {
            for (var col = 0; col < emulator.Cols; col++)
            {
                var cell = cells[row, col];
                if (!cell.IsWideContinuation) cell.AppendText(screen, replaceControlWithSpace: true);
                screen.Append(cell.Fg.IsDefault ? ' ' : '*');
            }

            screen.Append('\n');
        }

        return $"{screen}cursor {emulator.CursorRow},{emulator.CursorCol} visible {emulator.CursorVisible} shape {emulator.CursorShape}";
    }
}
