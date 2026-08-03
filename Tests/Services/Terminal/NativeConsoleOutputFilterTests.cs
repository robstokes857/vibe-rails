using System.Text;
using VibeRails.Services.Terminal.Consumers;
using Xunit;

namespace Tests.Services.Terminal;

/// <summary>
/// Pins the native-console relay filter. Session d2328427 (glm-5.2 Automation run, 2026-08-02):
/// opentui emitted ESC[8;41;156t (XTWINOPS "resize text area to 41x156") into its PTY output;
/// ConsoleOutputConsumer relayed it verbatim and conhost obeyed, resizing the real window
/// 120x30 -> 156x41. vb's 50 ms geometry poll then pushed that back into the inner PTY, and when
/// the console later snapped back to 120x30 it reflowed the alt-screen cells it had already
/// painted — which a diff-rendering TUI never repaints. See runbooks/terminal/TERMINAL.md
/// "## 2026-08-02 Automation runs in a native terminal…".
///
/// The filter is the one deliberate exception to the no-stripping rule and must stay narrow:
/// window *geometry* ops only, on the console relay only. Everything else — including every other
/// XTWINOPS op and every rendering sequence — passes through byte-for-byte.
///
/// ESC is spelled as the <see cref="Esc"/> constant throughout; never paste a raw 0x1B into this
/// file (byte-exact sources in this repo have been mangled by git's text filters before).
/// </summary>
public sealed class NativeConsoleOutputFilterTests
{
    private const string Esc = "\u001b";

    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

    private static string Filter(NativeConsoleOutputFilter filter, string input) =>
        Encoding.UTF8.GetString(filter.Filter(Bytes(input)));

    private static string FilterOnce(string input) => Filter(new NativeConsoleOutputFilter(), input);

    // ---- the reported bug -------------------------------------------------

    [Fact]
    public void Filter_DropsTheExactSequenceFromSessionD2328427()
    {
        var filter = new NativeConsoleOutputFilter();

        // Byte-for-byte the head of CHUNK 31757009 @ 2026-08-03T03:27:41.3307327Z.
        var result = Filter(filter, $"{Esc}[?25l{Esc}[8;41;156t{Esc}[38;2;255;255;255m");

        Assert.Equal($"{Esc}[?25l{Esc}[38;2;255;255;255m", result);
        Assert.Equal(1, filter.DroppedFrameCount);
    }

    [Theory]
    [InlineData(Esc + "[3;10;20t")]   // move window
    [InlineData(Esc + "[4;500;800t")] // resize in pixels
    [InlineData(Esc + "[8;41;156t")]  // resize in characters
    [InlineData(Esc + "[9;1t")]       // maximize
    [InlineData(Esc + "[10;1t")]      // fullscreen
    public void Filter_DropsWindowGeometryOps(string sequence)
    {
        var filter = new NativeConsoleOutputFilter();
        Assert.Equal("AB", Filter(filter, $"A{sequence}B"));
        Assert.Equal(1, filter.DroppedFrameCount);
    }

    [Theory]
    [InlineData(Esc + "[1t")]    // de-iconify
    [InlineData(Esc + "[2t")]    // iconify
    [InlineData(Esc + "[5t")]    // raise
    [InlineData(Esc + "[6t")]    // lower
    [InlineData(Esc + "[7t")]    // refresh
    [InlineData(Esc + "[14t")]   // report text area size in pixels (opentui sends this)
    [InlineData(Esc + "[18t")]   // report text area size in chars
    [InlineData(Esc + "[22;0t")] // push title
    [InlineData(Esc + "[23;0t")] // pop title
    [InlineData(Esc + "[t")]     // no parameter
    public void Filter_PassesThroughNonGeometryWindowOps(string sequence)
    {
        var filter = new NativeConsoleOutputFilter();
        Assert.Equal($"A{sequence}B", Filter(filter, $"A{sequence}B"));
        Assert.Equal(0, filter.DroppedFrameCount);
    }

    [Theory]
    [InlineData(Esc + "[?8;41;156t")] // private form is not XTWINOPS
    [InlineData(Esc + "[>8;41;156t")] // private form is not XTWINOPS
    [InlineData(Esc + "[8 t")]        // CSI Ps SP t = DECSWBV, not a window op
    public void Filter_LeavesLookalikeSequencesAlone(string sequence)
    {
        var filter = new NativeConsoleOutputFilter();
        Assert.Equal(sequence, Filter(filter, sequence));
        Assert.Equal(0, filter.DroppedFrameCount);
    }

    // ---- the no-stripping guardrail ---------------------------------------

    [Fact]
    public void Filter_LeavesRenderingSequencesByteForByte()
    {
        // Cursor ops, SGR, DEC private modes, alt screen, bracketed paste, sync output, OSC title,
        // mouse tracking — none of this may ever be touched.
        var stream =
            $"{Esc}[?1049h{Esc}[?2004h{Esc}[?2026h{Esc}[?25l" +
            $"{Esc}[38;2;201;209;217m{Esc}[48;2;13;17;23m{Esc}[29;4H" +
            $"{Esc}]0;vibe-rails{Esc}[?1000h{Esc}[?1002h{Esc}[?1006h" +
            $"{Esc}[2J{Esc}[H{Esc}[K{Esc}[120C{Esc}[?2026l{Esc}[?25h";

        var filter = new NativeConsoleOutputFilter();
        Assert.Equal(stream, Filter(filter, stream));
        Assert.Equal(0, filter.DroppedFrameCount);
    }

    [Fact]
    public void Filter_DoesNotMistakeALiteralTForASequence()
    {
        Assert.Equal("resize to 8;41;156t now", FilterOnce("resize to 8;41;156t now"));
    }

    // ---- chunk-boundary handling ------------------------------------------

    [Fact]
    public void Filter_DropsSequenceSplitAcrossPtyReads()
    {
        var filter = new NativeConsoleOutputFilter();

        // PTY reads land on arbitrary byte boundaries; the frame must still be recognised.
        var first = Filter(filter, $"A{Esc}[8;41");
        var second = Filter(filter, ";156tB");

        Assert.Equal("A", first);
        Assert.Equal("B", second);
        Assert.Equal(1, filter.DroppedFrameCount);
    }

    [Fact]
    public void Filter_DropsSequenceSplitOneByteAtATime()
    {
        var filter = new NativeConsoleOutputFilter();
        var output = new StringBuilder();

        foreach (var b in Bytes($"X{Esc}[8;41;156tY"))
            output.Append(Encoding.UTF8.GetString(filter.Filter([b])));

        Assert.Equal("XY", output.ToString());
        Assert.Equal(1, filter.DroppedFrameCount);
    }

    [Fact]
    public void Filter_HoldsBackPartialSequenceUntilItCompletes()
    {
        var filter = new NativeConsoleOutputFilter();

        Assert.Equal("A", Filter(filter, $"A{Esc}[14"));      // undecidable so far
        Assert.Equal($"{Esc}[14t", Filter(filter, "t"));      // a report — passes through
        Assert.Equal(0, filter.DroppedFrameCount);
    }

    [Fact]
    public void Filter_DoesNotSplitMultiByteGlyphsAcrossReads()
    {
        var filter = new NativeConsoleOutputFilter();

        // U+2B1D (the glyph opentui draws in its status row) is 3 bytes; split it 2/1.
        var glyph = Bytes("⬝");
        var first = filter.Filter(glyph.AsSpan(0, 2));
        var second = filter.Filter(glyph.AsSpan(2));

        var combined = Encoding.UTF8.GetString(first) + Encoding.UTF8.GetString(second);
        Assert.Equal("⬝", combined);
        Assert.DoesNotContain('�', combined);
    }

    [Fact]
    public void Filter_NeverStallsOnAnUnterminatedEscape()
    {
        var filter = new NativeConsoleOutputFilter();
        var junk = Esc + "[" + new string('9', NativeConsoleOutputFilter.MaxCarryBytes + 16);

        // A malformed frame must not hold console output hostage.
        var result = Filter(filter, junk);
        Assert.NotEmpty(result);
    }

    [Fact]
    public void Drain_ReturnsHeldBackBytes()
    {
        var filter = new NativeConsoleOutputFilter();

        Assert.Equal("A", Filter(filter, $"A{Esc}[8;41"));
        Assert.Equal($"{Esc}[8;41", Encoding.UTF8.GetString(filter.Drain()));
        Assert.Empty(filter.Drain());
    }

    // ---- aborted sequences -------------------------------------------------

    [Fact]
    public void Filter_DropsAGeometryOpThatFollowsAnAbortedSequence()
    {
        var filter = new NativeConsoleOutputFilter();

        // "ESC [ 1 2" is aborted by the next ESC, which is not a CSI final byte. Resuming *past*
        // that byte would swallow the ESC that starts the real XTWINOPS frame and relay it whole.
        var result = Filter(filter, $"{Esc}[12{Esc}[8;41;156tafter");

        Assert.Equal($"{Esc}[12after", result);
        Assert.Equal(1, filter.DroppedFrameCount);
    }

    [Fact]
    public void Filter_MakesProgressThroughRepeatedAbortedSequences()
    {
        var filter = new NativeConsoleOutputFilter();

        // Each ESC aborts the previous scan; the loop must still terminate and reach the last frame.
        var result = Filter(filter, $"{Esc}[1{Esc}[2{Esc}[3{Esc}[8;41;156tend");

        Assert.Equal($"{Esc}[1{Esc}[2{Esc}[3end", result);
        Assert.Equal(1, filter.DroppedFrameCount);
    }

    // ---- ordering ----------------------------------------------------------

    [Fact]
    public void Filter_PreservesSurroundingBytesAndOrderAcrossMultipleDrops()
    {
        var filter = new NativeConsoleOutputFilter();

        var result = Filter(filter,
            $"one{Esc}[8;41;156ttwo{Esc}[14tthree{Esc}[3;1;1tfour");

        Assert.Equal($"onetwo{Esc}[14tthreefour", result);
        Assert.Equal(2, filter.DroppedFrameCount);
    }
}
