using TerminalEmulator;

namespace TerminalEmulator.Tests;

/// <summary>
/// Unit tests for known ANSI sequences — fast, no PTY, no DB needed.
/// Each test feeds raw bytes and asserts grid/cursor state.
/// </summary>
public class AnsiParserTests
{
    private static Terminal Make(int cols = 80, int rows = 24) => new(cols, rows);

    // ------------------------------------------------------------------
    // Basic text output
    // ------------------------------------------------------------------

    [Fact]
    public void PlainText_WritesCharsToGrid()
    {
        var t = Make();
        t.Write("Hello");
        Assert.Equal('H', t.GetSnapshot()[0, 0].Char);
        Assert.Equal('e', t.GetSnapshot()[0, 1].Char);
        Assert.Equal('o', t.GetSnapshot()[0, 4].Char);
        Assert.Equal(5, t.CursorCol);
        Assert.Equal(0, t.CursorRow);
    }

    [Fact]
    public void CarriageReturn_MovesCursorToCol0()
    {
        var t = Make();
        t.Write("Hello\r");
        Assert.Equal(0, t.CursorCol);
    }

    [Fact]
    public void LineFeed_MovesCursorDown()
    {
        var t = Make();
        t.Write("Hello\n");
        Assert.Equal(1, t.CursorRow);
    }

    [Fact]
    public void CRLF_MovesToNextLine()
    {
        var t = Make();
        t.Write("Line1\r\nLine2");
        var snap = t.GetSnapshot();
        Assert.Equal('L', snap[0, 0].Char);
        Assert.Equal('L', snap[1, 0].Char);
        Assert.Equal(5, t.CursorCol);
        Assert.Equal(1, t.CursorRow);
    }

    [Fact]
    public void TextWraps_WhenExceedingCols()
    {
        var t = Make(cols: 5, rows: 5);
        t.Write("ABCDEFGH"); // 8 chars, wraps at col 5
        var snap = t.GetSnapshot();
        Assert.Equal('A', snap[0, 0].Char);
        Assert.Equal('E', snap[0, 4].Char);
        Assert.Equal('F', snap[1, 0].Char);
        Assert.Equal('H', snap[1, 2].Char);
    }

    // ------------------------------------------------------------------
    // Cursor movement
    // ------------------------------------------------------------------

    [Fact]
    public void CursorPosition_MovesToAbsolutePosition()
    {
        var t = Make();
        t.Write("\x1b[5;10H"); // row 5, col 10 (1-based)
        Assert.Equal(4, t.CursorRow);
        Assert.Equal(9, t.CursorCol);
    }

    [Fact]
    public void CursorUp_MovesUp()
    {
        var t = Make();
        t.Write("\x1b[5;1H"); // row 5
        t.Write("\x1b[2A");   // up 2
        Assert.Equal(2, t.CursorRow);
    }

    [Fact]
    public void CursorDown_MovesDown()
    {
        var t = Make();
        t.Write("\x1b[1;1H");
        t.Write("\x1b[3B"); // down 3
        Assert.Equal(3, t.CursorRow);
    }

    [Fact]
    public void CursorForward_MovesRight()
    {
        var t = Make();
        t.Write("\x1b[5C");
        Assert.Equal(5, t.CursorCol);
    }

    [Fact]
    public void CursorBack_MovesLeft()
    {
        var t = Make();
        t.Write("\x1b[10;10H");
        t.Write("\x1b[3D");
        Assert.Equal(6, t.CursorCol);
    }

    [Fact]
    public void CursorHome_NoParams_GoesToOrigin()
    {
        var t = Make();
        t.Write("\x1b[5;20H");
        t.Write("\x1b[H");
        Assert.Equal(0, t.CursorRow);
        Assert.Equal(0, t.CursorCol);
    }

    [Fact]
    public void SaveRestoreCursor_Works()
    {
        var t = Make();
        t.Write("\x1b[5;10H");
        t.Write("\u001b7");    // save (DECSC)
        t.Write("\x1b[1;1H"); // move elsewhere
        t.Write("\u001b8");    // restore (DECRC)
        Assert.Equal(4, t.CursorRow);
        Assert.Equal(9, t.CursorCol);
    }

    // ------------------------------------------------------------------
    // Erase operations
    // ------------------------------------------------------------------

    [Fact]
    public void EraseInDisplay2_ClearsWholeScreen()
    {
        var t = Make(cols: 10, rows: 5);
        t.Write("Hello\r\nWorld");
        t.Write("\x1b[2J");
        var lines = t.GetScreenText();
        Assert.All(lines, line => Assert.Equal(string.Empty, line));
    }

    [Fact]
    public void EraseInLine0_ErasesFromCursorToEnd()
    {
        var t = Make(cols: 10, rows: 5);
        t.Write("Hello");       // writes H e l l o at cols 0-4
        t.Write("\x1b[1;3H");   // cursor to row 1, col 3 (0-based: row 0, col 2)
        t.Write("\x1b[K");      // erase to end of line
        var snap = t.GetSnapshot();
        Assert.Equal('H', snap[0, 0].Char);
        Assert.Equal('e', snap[0, 1].Char);
        Assert.Equal(' ', snap[0, 2].Char);
        Assert.Equal(' ', snap[0, 4].Char);
    }

    [Fact]
    public void EraseInLine2_ErasesWholeLine()
    {
        var t = Make(cols: 10, rows: 5);
        t.Write("Hello");
        t.Write("\x1b[1;1H");
        t.Write("\x1b[2K");
        var snap = t.GetSnapshot();
        for (int c = 0; c < 10; c++)
            Assert.Equal(' ', snap[0, c].Char);
    }

    // ------------------------------------------------------------------
    // SGR colors and attributes
    // ------------------------------------------------------------------

    [Fact]
    public void Sgr_BoldAttribute()
    {
        var t = Make();
        t.Write("\x1b[1mA");
        var cell = t.GetSnapshot()[0, 0];
        Assert.Equal('A', cell.Char);
        Assert.True(cell.Attributes.HasFlag(CellAttributes.Bold));
    }

    [Fact]
    public void Sgr_Reset_ClearsAttributes()
    {
        var t = Make();
        t.Write("\x1b[1;32mA\x1b[0mB");
        var snap = t.GetSnapshot();
        Assert.True(snap[0, 0].Attributes.HasFlag(CellAttributes.Bold));
        Assert.False(snap[0, 1].Attributes.HasFlag(CellAttributes.Bold));
        Assert.True(snap[0, 1].Fg.IsDefault);
    }

    [Fact]
    public void Sgr_256Color_Foreground()
    {
        var t = Make();
        t.Write("\x1b[38;5;200mA");
        var cell = t.GetSnapshot()[0, 0];
        Assert.True(cell.Fg.IsPalette);
        Assert.Equal(200, cell.Fg.PaletteIndex);
    }

    [Fact]
    public void Sgr_TrueColor_Foreground()
    {
        var t = Make();
        t.Write("\x1b[38;2;215;119;87mA");
        var cell = t.GetSnapshot()[0, 0];
        Assert.True(cell.Fg.IsRgb);
        Assert.Equal(215, cell.Fg.R);
        Assert.Equal(119, cell.Fg.G);
        Assert.Equal(87, cell.Fg.B);
    }

    [Fact]
    public void Sgr_TrueColor_Background()
    {
        var t = Make();
        t.Write("\x1b[48;2;0;0;0mA");
        var cell = t.GetSnapshot()[0, 0];
        Assert.True(cell.Bg.IsRgb);
        Assert.Equal(0, cell.Bg.R);
        Assert.Equal(0, cell.Bg.G);
        Assert.Equal(0, cell.Bg.B);
    }

    // ------------------------------------------------------------------
    // Alternate screen
    // ------------------------------------------------------------------

    [Fact]
    public void AlternateScreen_ContentIsolatedFromNormal()
    {
        var t = Make(cols: 10, rows: 5);
        t.Write("NormalContent");
        t.Write("\x1b[?1049h"); // enter alternate
        Assert.True(t.IsAlternateScreen);
        var altSnap = t.GetSnapshot();
        Assert.Equal(' ', altSnap[0, 0].Char); // alternate screen is blank

        t.Write("\x1b[?1049l"); // exit alternate
        Assert.False(t.IsAlternateScreen);
        var normalSnap = t.GetSnapshot();
        Assert.Equal('N', normalSnap[0, 0].Char); // normal content preserved
    }

    // ------------------------------------------------------------------
    // Scrolling
    // ------------------------------------------------------------------

    [Fact]
    public void LineFeed_AtBottom_Scrolls()
    {
        var t = Make(cols: 10, rows: 3);
        t.Write("Line1\r\nLine2\r\nLine3\r\nLine4");
        var lines = t.GetScreenText();
        Assert.Equal("Line2", lines[0]);
        Assert.Equal("Line3", lines[1]);
        Assert.Equal("Line4", lines[2]);
    }

    // ------------------------------------------------------------------
    // Partial / split sequences (the main PTY bug)
    // ------------------------------------------------------------------

    [Fact]
    public void SplitEscapeSequence_ParsesCorrectly()
    {
        var t = Make();
        // Feed CSI cursor-up split across two chunks
        t.Write("\x1b[5;1H");        // position at row 5
        t.Write("\x1b[2");           // partial: ESC [ 2
        t.Write("A");                // complete: 2A = cursor up 2
        Assert.Equal(2, t.CursorRow);
    }

    [Fact]
    public void SplitUtf8_ParsesCorrectly()
    {
        var t = Make();
        // UTF-8 encode '€' (U+20AC) = E2 82 AC, split across chunks
        t.Write(new byte[] { 0xE2 });
        t.Write(new byte[] { 0x82 });
        t.Write(new byte[] { 0xAC });
        Assert.Equal('€', t.GetSnapshot()[0, 0].Char);
    }

    // ------------------------------------------------------------------
    // Dirty row tracking
    // ------------------------------------------------------------------

    [Fact]
    public void OnRender_FiresWithDirtyRows()
    {
        var t = Make(cols: 10, rows: 5);
        var firedRows = new List<int[]>();
        t.OnRender += rows => firedRows.Add(rows);

        t.Write("Hello"); // writes to row 0
        Assert.Single(firedRows);
        Assert.Contains(0, firedRows[0]);
    }

    [Fact]
    public void OnRender_MultipleRowsWhenScrolls()
    {
        var t = Make(cols: 5, rows: 3);
        var allDirty = new HashSet<int>();
        t.OnRender += rows => { foreach (var r in rows) allDirty.Add(r); };

        t.Write("AAAAA\r\nBBBBB\r\nCCCCC\r\nDDDDD"); // last \n causes scroll
        Assert.Contains(0, allDirty);
        Assert.Contains(1, allDirty);
        Assert.Contains(2, allDirty);
    }

    // ------------------------------------------------------------------
    // Resize
    // ------------------------------------------------------------------

    [Fact]
    public void Resize_PreservesContent()
    {
        var t = Make(cols: 10, rows: 5);
        t.Write("Hello");
        t.Resize(20, 10);
        Assert.Equal('H', t.GetSnapshot()[0, 0].Char);
        Assert.Equal(20, t.Cols);
        Assert.Equal(10, t.Rows);
    }

    // ------------------------------------------------------------------
    // Real-world sequences from the DB (hardcoded samples)
    // ------------------------------------------------------------------

    [Fact]
    public void RealWorld_PowerShellStartup_DoesNotThrow()
    {
        // First two chunks from session a0023034 as seen in the DB
        var t = Make(cols: 174, rows: 23);
        t.Write("\x1b[?9001h\x1b[?1004h\x1b[?25l\x1b[2J\x1b[m\x1b[HPowerShell 7.5.4\r\n]0;C:\\Program Files\\PowerShell\\7\\pwsh.exe\x1b[?25h");
        var lines = t.GetScreenText();
        Assert.Equal("PowerShell 7.5.4", lines[0]);
    }

    [Fact]
    public void RealWorld_TrueColorClaudeCodeLogo_DoesNotThrow()
    {
        // From DB row 183484 — Claude Code startup banner with true color
        var t = Make(cols: 174, rows: 23);
        var seq = "\x1b[38;2;215;119;87m \x1b[48;2;0;0;0m\x1b[m\x1b[1mClaude\x1b[22m\x1b[0m";
        Assert.True(true); // just assert no exception
        t.Write(seq);
    }

    [Fact]
    public void RealWorld_ResizeSequence()
    {
        // CSI 8;28;150t — resize to 28 rows, 150 cols
        var t = Make(cols: 80, rows: 24);
        t.Write("\x1b[8;28;150t");
        Assert.Equal(150, t.Cols);
        Assert.Equal(28, t.Rows);
    }
}
