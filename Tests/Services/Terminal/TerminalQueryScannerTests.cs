using System.Text;
using VibeRails.Services.Terminal;
using Xunit;

namespace Tests.Services.Terminal;

/// <summary>
/// VIBE-354: under a bundled console host the server answers device, cursor and mode queries, so
/// the scanner must find exactly the queries a terminal would answer and nothing that merely
/// looks like one. A missed query stalls the program; a false one types into it.
/// </summary>
public sealed class TerminalQueryScannerTests
{
    private const string Esc = "\u001b";

    [Theory]
    [InlineData("[c", nameof(TerminalQuery.PrimaryDeviceAttributes))]
    [InlineData("[0c", nameof(TerminalQuery.PrimaryDeviceAttributes))]
    [InlineData("[>c", nameof(TerminalQuery.SecondaryDeviceAttributes))]
    [InlineData("[>0c", nameof(TerminalQuery.SecondaryDeviceAttributes))]
    [InlineData("[5n", nameof(TerminalQuery.OperatingStatus))]
    [InlineData("[6n", nameof(TerminalQuery.CursorPosition))]
    [InlineData("[06n", nameof(TerminalQuery.CursorPosition))]
    [InlineData("[?6n", nameof(TerminalQuery.ExtendedCursorPosition))]
    // xterm.js reads only the first parameter of these, so a trailing one changes nothing.
    [InlineData("[6;1n", nameof(TerminalQuery.CursorPosition))]
    [InlineData("[0;5c", nameof(TerminalQuery.PrimaryDeviceAttributes))]
    // A zero first parameter is a request to xterm.js whatever follows it, so it is one here too.
    [InlineData("[>0;276;0c", nameof(TerminalQuery.SecondaryDeviceAttributes))]
    public void FindsTheQueriesTheServerAnswers(string sequence, string expected)
    {
        Assert.Equal([Enum.Parse<TerminalQuery>(expected)], Scan(Esc + sequence));
    }

    [Theory]
    [InlineData("[1c")]          // A device-attributes *report* parameter, not a request.
    [InlineData("[=c")]          // Tertiary device attributes: xterm.js does not answer it.
    [InlineData("[?c")]
    [InlineData("[?1;2c")]       // The answer itself, echoed by a program, is not a question.
    [InlineData("[7n")]
    [InlineData("[?15n")]
    [InlineData("[?5n")]
    [InlineData("[>6n")]
    [InlineData("[6$n")]         // An intermediate byte makes it a different function.
    [InlineData("[ c")]
    [InlineData("[0?6n")]        // A private marker is only one directly after CSI.
    [InlineData("[6?n")]
    [InlineData("[!p")]          // DECSTR soft reset shares the final byte of a mode query.
    [InlineData("[62\"p")]       // DECSCL too.
    [InlineData("[?25$ p")]      // Two intermediates: not DECRQM.
    [InlineData("[?$25p")]       // A parameter after the intermediate is malformed.
    [InlineData("[>4$p")]
    [InlineData("[>q")]          // XTVERSION: xterm.js does not answer it, and neither do we.
    [InlineData("[18t")]
    [InlineData("[6;9R")]
    [InlineData("[H")]
    [InlineData("c")]
    [InlineData("6n")]
    public void IgnoresEverythingElse(string sequence)
    {
        Assert.Empty(Scan(Esc + sequence));
    }

    [Theory]
    [InlineData("[?2026$p", nameof(TerminalQuery.PrivateMode), 2026)]
    [InlineData("[?25$p", nameof(TerminalQuery.PrivateMode), 25)]
    [InlineData("[?$p", nameof(TerminalQuery.PrivateMode), 0)]
    [InlineData("[?1049;7$p", nameof(TerminalQuery.PrivateMode), 1049)]
    [InlineData("[4$p", nameof(TerminalQuery.AnsiMode), 4)]
    [InlineData("[$p", nameof(TerminalQuery.AnsiMode), 0)]
    public void AModeQueryCarriesTheModeAskedAbout(string sequence, string expected, int mode)
    {
        var scanner = new TerminalQueryScanner();
        var bytes = Encoding.ASCII.GetBytes(Esc + sequence);

        Assert.Equal(bytes.Length, scanner.Scan(bytes, out var query, out var parameter));
        Assert.Equal(Enum.Parse<TerminalQuery>(expected), query);
        Assert.Equal(mode, parameter);
    }

    [Fact]
    public void ReportsEachQueryAtItsFinalByteSoTheCallerCanStopThere()
    {
        var bytes = Encoding.ASCII.GetBytes($"abc{Esc}[6ndef{Esc}[cxyz");
        var scanner = new TerminalQueryScanner();

        var first = scanner.Scan(bytes, out var firstQuery, out _);
        Assert.Equal(TerminalQuery.CursorPosition, firstQuery);
        Assert.Equal("abc\u001b[6n".Length, first);

        var second = scanner.Scan(bytes.AsSpan(first), out var secondQuery, out _);
        Assert.Equal(TerminalQuery.PrimaryDeviceAttributes, secondQuery);
        Assert.Equal("def\u001b[c".Length, second);

        var rest = scanner.Scan(bytes.AsSpan(first + second), out var none, out _);
        Assert.Equal(TerminalQuery.None, none);
        Assert.Equal(3, rest);
    }

    [Fact]
    public void FindsAQuerySplitAcrossReadsAtEveryPossiblePoint()
    {
        var bytes = Encoding.ASCII.GetBytes($"text{Esc}[?6nmore");
        for (var split = 1; split < bytes.Length; split++)
        {
            var scanner = new TerminalQueryScanner();
            var found = new List<TerminalQuery>();
            Collect(ref scanner, bytes.AsSpan(0, split), found);
            Collect(ref scanner, bytes.AsSpan(split), found);
            Assert.Equal([TerminalQuery.ExtendedCursorPosition], found);
        }
    }

    [Theory]
    // An OSC title or a DCS payload may contain the letters of a query. It is text, not a query.
    [InlineData("\u001b]0;[6n and [c\u0007")]
    [InlineData("\u001b]0;[6n\u001b\\")]
    [InlineData("\u001bP$q[6n\u001b\\")]
    [InlineData("\u001b_[c\u001b\\")]
    [InlineData("\u001b^[5n\u001b\\")]
    [InlineData("\u001bX[6n\u001b\\")]
    public void TextInsideAStringSequenceIsNeverAQuery(string output)
    {
        Assert.Empty(Scan(output));
    }

    [Fact]
    public void AQueryAfterAStringSequenceIsFound()
    {
        Assert.Equal([TerminalQuery.CursorPosition], Scan("\u001b]0;title\u0007\u001b[6n"));
        Assert.Equal([TerminalQuery.PrimaryDeviceAttributes], Scan("\u001bPpayload\u001b\\\u001b[c"));
    }

    [Fact]
    public void EscapeInsideAStringEndsItTheWayTheEmulatorAndXtermDo()
    {
        // ESC always starts over, so this is an unterminated title followed by a real query.
        Assert.Equal([TerminalQuery.CursorPosition], Scan("\u001b]0;title\u001b[6n"));
    }

    [Fact]
    public void ControlCharactersInsideASequenceActWithoutEndingIt()
    {
        Assert.Equal([TerminalQuery.CursorPosition], Scan("\u001b[\r6\nn"));
        Assert.Equal([TerminalQuery.CursorPosition], Scan("\u001b[6\u007fn"));
    }

    [Theory]
    [InlineData("\u001b[6\u0018n")]   // CAN aborts.
    [InlineData("\u001b[6\u001an")]   // SUB aborts.
    [InlineData("\u001b[6\u00e9n")]   // A byte that cannot be part of a sequence aborts.
    public void AnAbortedSequenceIsNotAQuery(string output)
    {
        Assert.Empty(Scan(output, Encoding.Latin1));
    }

    [Fact]
    public void EscapeRestartsASequence()
    {
        Assert.Equal([TerminalQuery.PrimaryDeviceAttributes], Scan("\u001b[6\u001b[c"));
        Assert.Equal([TerminalQuery.CursorPosition], Scan("\u001b\u001b[6n"));
    }

    [Fact]
    public void AnOversizedParameterNeverWrapsIntoAQuery()
    {
        Assert.Empty(Scan("\u001b[4294967302n"));       // 2^32 + 6
        Assert.Empty(Scan("\u001b[" + new string('9', 40) + "n"));
        Assert.Empty(Scan("\u001b[18446744073709551616c")); // 2^64, which would wrap to zero
        // A mode number is echoed in the answer, so an absurd one is not answered at all.
        Assert.Empty(Scan("\u001b[?4294969322$p"));     // 2^32 + 2026
        Assert.Empty(Scan("\u001b[?10000$p"));
        Assert.Equal([TerminalQuery.PrivateMode], Scan("\u001b[?9999$p"));
    }

    [Fact]
    public void OrdinaryOutputProducesNothing()
    {
        var frame = "\u001b[?2026h\u001b[?25l\u001b[3;1HStatus line text\u001b[38;2;255;100;0m\u001b[K\u001b[?25h\u001b[?2026l\r\nPS C:\\> ";
        Assert.Empty(Scan(frame));
    }

    private static List<TerminalQuery> Scan(string output, Encoding? encoding = null)
    {
        var scanner = new TerminalQueryScanner();
        var found = new List<TerminalQuery>();
        Collect(ref scanner, (encoding ?? Encoding.UTF8).GetBytes(output), found);
        return found;
    }

    private static void Collect(ref TerminalQueryScanner scanner, ReadOnlySpan<byte> data, List<TerminalQuery> found)
    {
        while (!data.IsEmpty)
        {
            var read = scanner.Scan(data, out var query, out _);
            if (query != TerminalQuery.None) found.Add(query);
            data = data[read..];
        }
    }
}
