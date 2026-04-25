using TerminalEmulator;

namespace TerminalEmulator.Tests;

/// <summary>
/// Regression for session bfc6c3c5: the OSC parser was treating the byte 0x9C
/// (8-bit String Terminator in C1 controls) as an OSC terminator. But 0x9C is
/// also a valid UTF-8 continuation byte, so any OSC payload containing a
/// multi-byte UTF-8 glyph whose continuation hits 0x9C was cut short
/// mid-character — and the remainder of the payload (plus the real BEL/ST)
/// leaked onto the screen as visible text.
///
/// Concrete failure observed on 2026-04-25: Claude Code emits its window title
/// as <c>\x1B]0;✳ Claude Code\x07</c>. The glyph ✳ (U+2733) is encoded in
/// UTF-8 as <c>0xE2 0x9C 0xB3</c>. The parser saw the 0x9C, dispatched the
/// OSC mid-payload, returned to ground, then printed " Claude Code" verbatim
/// right where the cursor sat — directly before Claude Code drew its splash
/// banner. The user saw <c>Claude Code╭─── Claude Code v2.1.119 ───╮</c>
/// instead of <c>╭─── Claude Code v2.1.119 ───╮</c>.
///
/// The fix: do NOT honor the 8-bit ST (0x9C) as an OSC terminator. Modern
/// terminal output is UTF-8, and 0x9C overlaps with continuation bytes. Only
/// BEL (0x07) and ESC \ (the 7-bit ST form) are accepted. Same applies to
/// DCS / SOS / PM / APC passthrough strings.
/// </summary>
public class OscUtf8PayloadTests
{
    private static Terminal Make(int cols = 80, int rows = 24) => new(cols, rows);

    // The sequence Claude Code actually sent in session bfc6c3c5.
    private static readonly byte[] ClaudeTitleOsc =
    [
        0x1B, 0x5D, 0x30, 0x3B,           // ESC ] 0 ;
        0xE2, 0x9C, 0xB3,                 // ✳ (U+2733) — middle byte 0x9C
        0x20, 0x43, 0x6C, 0x61, 0x75, 0x64, 0x65, 0x20, 0x43, 0x6F, 0x64, 0x65, // " Claude Code"
        0x07                              // BEL — real terminator
    ];

    [Fact]
    public void OscWithUtf8Payload_DoesNotLeakIntoScreen()
    {
        var t = Make();
        t.Write(ClaudeTitleOsc.AsSpan());

        // Cursor must not have advanced — OSC sets the title, it is not visible output.
        Assert.Equal(0, t.CursorCol);
        Assert.Equal(0, t.CursorRow);

        // No cell on row 0 should contain any of the leaked title text.
        var snap = t.GetSnapshot();
        for (int c = 0; c < t.Cols; c++)
        {
            Assert.NotEqual('C', snap[0, c].Char);
            Assert.NotEqual('l', snap[0, c].Char);
            Assert.NotEqual('a', snap[0, c].Char);
        }
    }

    [Fact]
    public void OscWithUtf8Payload_FollowedByPrintedText_StartsAtColumn0()
    {
        // Mirrors the real session: cursor sits at col 0, OSC title arrives,
        // then Claude Code starts drawing its banner top-border `╭` (U+256D, UTF-8
        // 0xE2 0x95 0xAD). The banner must land at col 0, not after a leaked
        // " Claude Code" prefix.
        var t = Make();
        t.Write(ClaudeTitleOsc.AsSpan());
        t.Write([0xE2, 0x95, 0xAD]); // ╭

        var snap = t.GetSnapshot();
        Assert.Equal('╭', snap[0, 0].Char);
    }

    [Fact]
    public void OscWith0x9CInBody_DoesNotEarlyTerminate_BelTerminates()
    {
        // Defensive: any OSC body that happens to contain 0x9C (whether from
        // UTF-8 or other byte sequences) must be consumed up to the real BEL.
        var t = Make();
        Span<byte> payload = stackalloc byte[]
        {
            0x1B, 0x5D, 0x30, 0x3B,           // ESC ] 0 ;
            (byte)'A', 0x9C, (byte)'B',       // payload "A<0x9C>B"
            0x07,                             // BEL
            (byte)'X'                         // visible content after OSC ends
        };
        t.Write(payload);

        var snap = t.GetSnapshot();
        Assert.Equal('X', snap[0, 0].Char);   // X must land at col 0, not after leak
        Assert.Equal(1, t.CursorCol);
    }

    [Fact]
    public void OscWithEscBackslashTerminator_StillWorks()
    {
        // ESC \ (7-bit ST) must continue to terminate OSC strings.
        var t = Make();
        Span<byte> payload = stackalloc byte[]
        {
            0x1B, 0x5D, 0x30, 0x3B,           // ESC ] 0 ;
            (byte)'h', (byte)'i',             // payload "hi"
            0x1B, 0x5C,                       // ESC \ (ST)
            (byte)'Y'                         // visible content after OSC ends
        };
        t.Write(payload);

        var snap = t.GetSnapshot();
        Assert.Equal('Y', snap[0, 0].Char);
        Assert.Equal(1, t.CursorCol);
    }
}
