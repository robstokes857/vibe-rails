using TerminalEmulator;

namespace TerminalEmulator.Tests;

/// <summary>
/// Comprehensive tests for every VT/ANSI/xterm escape sequence.
///
/// References:
///   https://xtermjs.org/docs/api/vtfeatures/
///   https://gist.github.com/fnky/458719343aabd01cfb17a3a4f7296797
/// </summary>
public class EscapeSequenceTests
{
    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static Terminal Make(int cols = 80, int rows = 24) => new Terminal(cols, rows);

    // NOTE: Use \u001B (4-digit Unicode) for ESC, NOT \x1B, because C# \x is greedy:
    // "\x1BD" parses as codepoint 0x1BD (not ESC+'D'). \u001B always = exactly ESC.
    private static void Write(Terminal t, string s)
    {
        var bytes = new byte[s.Length];
        for (int i = 0; i < s.Length; i++)
            bytes[i] = (byte)s[i];
        t.Write(bytes.AsSpan());
    }

    private static char Char(Terminal t, int row, int col) => t.GetSnapshot()[row, col].Char;
    private static TerminalCell Cell(Terminal t, int row, int col) => t.GetSnapshot()[row, col];

    // Empty cell character — TerminalCell.Empty.Char is ' ' (space)
    private const char E = ' ';

    // ------------------------------------------------------------------
    // C0 Control characters
    // ------------------------------------------------------------------

    [Fact]
    public void NUL_IsIgnored()
    {
        var t = Make();
        t.Write(new byte[] { (byte)'A', 0x00, (byte)'B' }.AsSpan());
        Assert.Equal('A', Char(t, 0, 0));
        Assert.Equal('B', Char(t, 0, 1));
        Assert.Equal(2, t.CursorCol);
    }

    [Fact]
    public void BEL_IsIgnored()
    {
        var t = Make();
        t.Write(new byte[] { (byte)'A', 0x07, (byte)'B' }.AsSpan());
        Assert.Equal('A', Char(t, 0, 0));
        Assert.Equal('B', Char(t, 0, 1));
        Assert.Equal(2, t.CursorCol);
    }

    [Fact]
    public void BS_MovesLeft()
    {
        var t = Make();
        Write(t, "AB\b");
        Assert.Equal(1, t.CursorCol);
    }

    [Fact]
    public void BS_AtColumn0_StaysAt0()
    {
        var t = Make();
        Write(t, "\b\b");
        Assert.Equal(0, t.CursorCol);
    }

    [Fact]
    public void HT_ForwardToNextTabStop_From0()
    {
        var t = Make();
        Write(t, "\t");
        Assert.Equal(8, t.CursorCol);
    }

    [Fact]
    public void HT_ForwardToNextTabStop_FromMiddle()
    {
        var t = Make();
        Write(t, "ABC\t");
        Assert.Equal(8, t.CursorCol);
    }

    [Fact]
    public void HT_MultipleTabStops()
    {
        var t = Make();
        Write(t, "\t\t");
        Assert.Equal(16, t.CursorCol);
    }

    [Fact]
    public void HT_ClampAtLastCol()
    {
        var t = Make(cols: 10);
        Write(t, "\t\t");   // 8, then next stop 16 > 9 → clamped at 9
        Assert.Equal(9, t.CursorCol);
    }

    [Fact]
    public void LF_MovesDown()
    {
        var t = Make();
        Write(t, "\n");
        Assert.Equal(1, t.CursorRow);
        Assert.Equal(0, t.CursorCol);
    }

    [Fact]
    public void VT_SameAsLF()
    {
        var t = Make();
        t.Write(new byte[] { 0x0B }.AsSpan());
        Assert.Equal(1, t.CursorRow);
    }

    [Fact]
    public void FF_SameAsLF()
    {
        var t = Make();
        t.Write(new byte[] { 0x0C }.AsSpan());
        Assert.Equal(1, t.CursorRow);
    }

    [Fact]
    public void CR_MovesToColumn0()
    {
        var t = Make();
        Write(t, "HELLO\r");
        Assert.Equal(0, t.CursorCol);
        Assert.Equal(0, t.CursorRow);
    }

    [Fact]
    public void SO_SI_Ignored()
    {
        var t = Make();
        t.Write(new byte[] { (byte)'A', 0x0E, 0x0F, (byte)'B' }.AsSpan());
        Assert.Equal('A', Char(t, 0, 0));
        Assert.Equal('B', Char(t, 0, 1));
    }

    [Fact]
    public void DEL_Ignored()
    {
        var t = Make();
        t.Write(new byte[] { (byte)'A', 0x7F, (byte)'B' }.AsSpan());
        Assert.Equal('A', Char(t, 0, 0));
        Assert.Equal('B', Char(t, 0, 1));
        Assert.Equal(2, t.CursorCol);
    }

    // ------------------------------------------------------------------
    // LF scrolling
    // ------------------------------------------------------------------

    [Fact]
    public void LF_AtBottom_ScrollsUp()
    {
        var t = Make(cols: 5, rows: 3);
        Write(t, "A\r\nB\r\nC\r\nD");
        Assert.Equal('B', Char(t, 0, 0));
        Assert.Equal('C', Char(t, 1, 0));
        Assert.Equal('D', Char(t, 2, 0));
    }

    // ------------------------------------------------------------------
    // ESC sequences — use \u001B (not \x1B) when next char is a hex digit
    // ------------------------------------------------------------------

    [Fact]
    public void ESC_D_IND_MovesDown()
    {
        var t = Make();
        Write(t, "\u001BD");
        Assert.Equal(1, t.CursorRow);
        Assert.Equal(0, t.CursorCol);
    }

    [Fact]
    public void ESC_D_IND_ScrollsAtBottom()
    {
        var t = Make(cols: 5, rows: 3);
        Write(t, "A\r\nB\r\nC");
        Write(t, "\u001BD");
        Assert.Equal('B', Char(t, 0, 0));
        Assert.Equal('C', Char(t, 1, 0));
        Assert.Equal(2, t.CursorRow);
    }

    [Fact]
    public void ESC_E_NEL_NextLine()
    {
        var t = Make();
        Write(t, "ABC\u001BE");
        Assert.Equal(1, t.CursorRow);
        Assert.Equal(0, t.CursorCol);
    }

    [Fact]
    public void ESC_E_NEL_ScrollsAtBottom()
    {
        var t = Make(cols: 5, rows: 2);
        Write(t, "AAAAA\r\nBBBBB");
        Write(t, "\u001BE");
        Assert.Equal('B', Char(t, 0, 0));
        Assert.Equal(E, Char(t, 1, 0));
        Assert.Equal(1, t.CursorRow);
        Assert.Equal(0, t.CursorCol);
    }

    [Fact]
    public void ESC_H_HTS_SetsTabStop_At_Custom_Col()
    {
        var t = Make();
        Write(t, "\u001B[1;6H");   // CUP row 1, col 6 (1-based = col 5 zero-based)
        Write(t, "\u001BH");       // HTS at col 5
        Write(t, "\u001B[H");      // home
        Write(t, "\t");            // HT — skip col 0 stop, next stop should be 5
        Assert.Equal(5, t.CursorCol);
    }

    [Fact]
    public void ESC_M_RI_ReverseIndex_MovesUp()
    {
        var t = Make();
        Write(t, "\n\n");
        Write(t, "\u001BM");
        Assert.Equal(1, t.CursorRow);
    }

    [Fact]
    public void ESC_M_RI_ScrollsAtTop()
    {
        var t = Make(cols: 5, rows: 3);
        Write(t, "A\r\nB\r\nC");
        Write(t, "\u001B[H");       // home to row 0
        Write(t, "\u001BM");        // RI at top — inserts blank line
        Assert.Equal(E, Char(t, 0, 0));
        Assert.Equal('A', Char(t, 1, 0));
        Assert.Equal('B', Char(t, 2, 0));
    }

    [Fact]
    public void ESC_7_DECSC_And_ESC_8_DECRC_SaveRestoreCursor()
    {
        var t = Make();
        Write(t, "\u001B[5;10H");   // row 5, col 10 (1-based)
        Write(t, "\u001B7");        // DECSC save
        Write(t, "\u001B[1;1H");    // move away
        Write(t, "\u001B8");        // DECRC restore
        Assert.Equal(4, t.CursorRow);
        Assert.Equal(9, t.CursorCol);
    }

    [Fact]
    public void ESC_c_RIS_FullReset()
    {
        var t = Make();
        Write(t, "HELLO\u001B[3;5H");
        Write(t, "\u001Bc");
        Assert.Equal(0, t.CursorRow);
        Assert.Equal(0, t.CursorCol);
        Assert.Equal(E, Char(t, 0, 0));
    }

    // ------------------------------------------------------------------
    // CSI cursor movement — CUU, CUD, CUF, CUB
    // ------------------------------------------------------------------

    [Fact]
    public void CSI_A_CUU_MovesUp()
    {
        var t = Make();
        Write(t, "\u001B[5;1H\u001B[2A");
        Assert.Equal(2, t.CursorRow);
    }

    [Fact]
    public void CSI_A_CUU_Default1()
    {
        var t = Make();
        Write(t, "\u001B[3;1H\u001B[A");
        Assert.Equal(1, t.CursorRow);
    }

    [Fact]
    public void CSI_A_CUU_ClampsAt0()
    {
        var t = Make();
        Write(t, "\u001B[3;1H\u001B[100A");
        Assert.Equal(0, t.CursorRow);
    }

    [Fact]
    public void CSI_B_CUD_MovesDown()
    {
        var t = Make();
        Write(t, "\u001B[3B");
        Assert.Equal(3, t.CursorRow);
    }

    [Fact]
    public void CSI_B_CUD_ClampsAtBottom()
    {
        var t = Make(rows: 5);
        Write(t, "\u001B[100B");
        Assert.Equal(4, t.CursorRow);
    }

    [Fact]
    public void CSI_C_CUF_MovesRight()
    {
        var t = Make();
        Write(t, "\u001B[5C");
        Assert.Equal(5, t.CursorCol);
    }

    [Fact]
    public void CSI_C_CUF_Default1()
    {
        var t = Make();
        Write(t, "\u001B[C");
        Assert.Equal(1, t.CursorCol);
    }

    [Fact]
    public void CSI_D_CUB_MovesLeft()
    {
        var t = Make();
        Write(t, "\u001B[10C\u001B[3D");
        Assert.Equal(7, t.CursorCol);
    }

    [Fact]
    public void CSI_D_CUB_ClampsAt0()
    {
        var t = Make();
        Write(t, "\u001B[100D");
        Assert.Equal(0, t.CursorCol);
    }

    // ------------------------------------------------------------------
    // CSI cursor movement — CNL, CPL, CHA, CUP, CHT, CBT
    // ------------------------------------------------------------------

    [Fact]
    public void CSI_E_CNL_CursorNextLine()
    {
        var t = Make();
        Write(t, "\u001B[5;10H\u001B[2E");
        Assert.Equal(6, t.CursorRow);
        Assert.Equal(0, t.CursorCol);
    }

    [Fact]
    public void CSI_F_CPL_CursorPrevLine()
    {
        var t = Make();
        Write(t, "\u001B[5;10H\u001B[2F");
        Assert.Equal(2, t.CursorRow);
        Assert.Equal(0, t.CursorCol);
    }

    [Fact]
    public void CSI_G_CHA_SetColumn()
    {
        var t = Make();
        Write(t, "\u001B[3;1H\u001B[15G");
        Assert.Equal(14, t.CursorCol);
    }

    [Fact]
    public void CSI_G_CHA_Default1_GoesToCol0()
    {
        var t = Make();
        Write(t, "HELLO\u001B[G");
        Assert.Equal(0, t.CursorCol);
    }

    [Fact]
    public void CSI_H_CUP_SetPosition()
    {
        var t = Make();
        Write(t, "\u001B[10;20H");
        Assert.Equal(9, t.CursorRow);
        Assert.Equal(19, t.CursorCol);
    }

    [Fact]
    public void CSI_H_CUP_Default_GoesHome()
    {
        var t = Make();
        Write(t, "\u001B[5;5H\u001B[H");
        Assert.Equal(0, t.CursorRow);
        Assert.Equal(0, t.CursorCol);
    }

    [Fact]
    public void CSI_f_HVP_SameAsCUP()
    {
        var t = Make();
        Write(t, "\u001B[10;20f");
        Assert.Equal(9, t.CursorRow);
        Assert.Equal(19, t.CursorCol);
    }

    [Fact]
    public void CSI_I_CHT_CursorForwardTab()
    {
        var t = Make();
        Write(t, "\u001B[2I");
        Assert.Equal(16, t.CursorCol);
    }

    [Fact]
    public void CSI_I_CHT_Default1()
    {
        var t = Make();
        Write(t, "\u001B[I");
        Assert.Equal(8, t.CursorCol);
    }

    [Fact]
    public void CSI_Z_CBT_CursorBackTab()
    {
        var t = Make();
        Write(t, "\u001B[20G");   // col 20 (1-based) = col 19 (0-based)
        Write(t, "\u001B[Z");     // back 1 tab stop
        Assert.Equal(16, t.CursorCol);
    }

    [Fact]
    public void CSI_Z_CBT_Multiple()
    {
        var t = Make();
        Write(t, "\u001B[25G");   // col 24 (0-based)
        Write(t, "\u001B[2Z");
        Assert.Equal(8, t.CursorCol);
    }

    [Fact]
    public void CSI_Z_CBT_ClampsAt0()
    {
        var t = Make();
        Write(t, "\u001B[5C\u001B[100Z");
        Assert.Equal(0, t.CursorCol);
    }

    // ------------------------------------------------------------------
    // CSI cursor movement — HPR, VPA, VPR, HPA
    // ------------------------------------------------------------------

    [Fact]
    public void CSI_a_HPR_CursorRight()
    {
        var t = Make();
        Write(t, "\u001B[5;1H\u001B[3a");
        Assert.Equal(3, t.CursorCol);
    }

    [Fact]
    public void CSI_d_VPA_SetRow()
    {
        var t = Make();
        Write(t, "\u001B[15d");
        Assert.Equal(14, t.CursorRow);
    }

    [Fact]
    public void CSI_d_VPA_Default1_GoesToRow0()
    {
        var t = Make();
        Write(t, "\u001B[5;1H\u001B[d");
        Assert.Equal(0, t.CursorRow);
    }

    [Fact]
    public void CSI_e_VPR_CursorDown()
    {
        var t = Make();
        Write(t, "\u001B[3;5H\u001B[2e");
        Assert.Equal(4, t.CursorRow);
        Assert.Equal(4, t.CursorCol);
    }

    [Fact]
    public void CSI_backtick_HPA_SetColumn()
    {
        var t = Make();
        Write(t, "\u001B[3;1H\u001B[10`");
        Assert.Equal(9, t.CursorCol);
    }

    // ------------------------------------------------------------------
    // CSI erase — ED
    // ------------------------------------------------------------------

    [Fact]
    public void CSI_J_ED0_EraseToEnd()
    {
        var t = Make(cols: 5, rows: 3);
        Write(t, "ABCDE\r\nFGHIJ\r\nKLMNO");
        Write(t, "\u001B[2;3H");   // row 2, col 3 (1-based) → [1,2]
        Write(t, "\u001B[J");      // ED 0
        var s = t.GetSnapshot();
        Assert.Equal('F', s[1, 0].Char);
        Assert.Equal('G', s[1, 1].Char);
        Assert.Equal(E, s[1, 2].Char);
        Assert.Equal(E, s[2, 0].Char);
    }

    [Fact]
    public void CSI_J_ED1_EraseToBeg()
    {
        var t = Make(cols: 5, rows: 3);
        Write(t, "ABCDE\r\nFGHIJ\r\nKLMNO");
        Write(t, "\u001B[2;3H");
        Write(t, "\u001B[1J");
        var s = t.GetSnapshot();
        Assert.Equal(E, s[0, 0].Char);
        Assert.Equal(E, s[1, 0].Char);
        Assert.Equal(E, s[1, 2].Char);   // H is at col 2 — erased
        Assert.Equal('I', s[1, 3].Char); // col 3+ preserved
        Assert.Equal('K', s[2, 0].Char);
    }

    [Fact]
    public void CSI_J_ED2_EraseWhole()
    {
        var t = Make(cols: 5, rows: 3);
        Write(t, "ABCDE\r\nFGHIJ\r\nKLMNO");
        Write(t, "\u001B[2J");
        var s = t.GetSnapshot();
        for (int r = 0; r < 3; r++)
            for (int c = 0; c < 5; c++)
                Assert.Equal(E, s[r, c].Char);
    }

    // ------------------------------------------------------------------
    // CSI erase — EL
    // ------------------------------------------------------------------

    [Fact]
    public void CSI_K_EL0_EraseToEndOfLine()
    {
        var t = Make(cols: 5, rows: 3);
        Write(t, "ABCDE");
        Write(t, "\u001B[1;3H");   // col 3 (1-based) = col 2 (0-based)
        Write(t, "\u001B[K");
        var s = t.GetSnapshot();
        Assert.Equal('A', s[0, 0].Char);
        Assert.Equal('B', s[0, 1].Char);
        Assert.Equal(E, s[0, 2].Char);
        Assert.Equal(E, s[0, 4].Char);
    }

    [Fact]
    public void CSI_K_EL1_EraseToBegOfLine()
    {
        var t = Make(cols: 5, rows: 1);
        Write(t, "ABCDE");
        Write(t, "\u001B[1;3H");
        Write(t, "\u001B[1K");
        var s = t.GetSnapshot();
        Assert.Equal(E, s[0, 0].Char);
        Assert.Equal(E, s[0, 2].Char);
        Assert.Equal('D', s[0, 3].Char);
    }

    [Fact]
    public void CSI_K_EL2_EraseWholeLine()
    {
        var t = Make(cols: 5, rows: 2);
        Write(t, "ABCDE\r\nFGHIJ");
        Write(t, "\u001B[1;1H");
        Write(t, "\u001B[2K");
        var s = t.GetSnapshot();
        for (int c = 0; c < 5; c++)
            Assert.Equal(E, s[0, c].Char);
        Assert.Equal('F', s[1, 0].Char);
    }

    // ------------------------------------------------------------------
    // CSI erase — ECH
    // ------------------------------------------------------------------

    [Fact]
    public void CSI_X_ECH_EraseNChars()
    {
        var t = Make(cols: 10, rows: 1);
        Write(t, "ABCDEFGHIJ");
        Write(t, "\u001B[1;3H");   // col 2 (0-based)
        Write(t, "\u001B[3X");
        var s = t.GetSnapshot();
        Assert.Equal('A', s[0, 0].Char);
        Assert.Equal('B', s[0, 1].Char);
        Assert.Equal(E, s[0, 2].Char);
        Assert.Equal(E, s[0, 4].Char);
        Assert.Equal('F', s[0, 5].Char);
        Assert.Equal(2, t.CursorCol);
    }

    // ------------------------------------------------------------------
    // CSI insert/delete — IL, DL, ICH, DCH
    // ------------------------------------------------------------------

    [Fact]
    public void CSI_L_IL_InsertLines()
    {
        var t = Make(cols: 5, rows: 4);
        Write(t, "AAAAA\r\nBBBBB\r\nCCCCC\r\nDDDDD");
        Write(t, "\u001B[2;1H");
        Write(t, "\u001B[2L");
        var s = t.GetSnapshot();
        Assert.Equal('A', s[0, 0].Char);
        Assert.Equal(E, s[1, 0].Char);
        Assert.Equal(E, s[2, 0].Char);
        Assert.Equal('B', s[3, 0].Char);
    }

    [Fact]
    public void CSI_M_DL_DeleteLines()
    {
        var t = Make(cols: 5, rows: 4);
        Write(t, "AAAAA\r\nBBBBB\r\nCCCCC\r\nDDDDD");
        Write(t, "\u001B[2;1H");
        Write(t, "\u001B[2M");
        var s = t.GetSnapshot();
        Assert.Equal('A', s[0, 0].Char);
        Assert.Equal('D', s[1, 0].Char);
        Assert.Equal(E, s[2, 0].Char);
        Assert.Equal(E, s[3, 0].Char);
    }

    [Fact]
    public void CSI_at_ICH_InsertChars()
    {
        var t = Make(cols: 5, rows: 1);
        Write(t, "ABCDE");
        Write(t, "\u001B[1;2H");
        Write(t, "\u001B[2@");
        var s = t.GetSnapshot();
        Assert.Equal('A', s[0, 0].Char);
        Assert.Equal(E, s[0, 1].Char);
        Assert.Equal(E, s[0, 2].Char);
        Assert.Equal('B', s[0, 3].Char);
        Assert.Equal('C', s[0, 4].Char);
    }

    [Fact]
    public void CSI_P_DCH_DeleteChars()
    {
        var t = Make(cols: 5, rows: 1);
        Write(t, "ABCDE");
        Write(t, "\u001B[1;2H");
        Write(t, "\u001B[2P");
        var s = t.GetSnapshot();
        Assert.Equal('A', s[0, 0].Char);
        Assert.Equal('D', s[0, 1].Char);
        Assert.Equal('E', s[0, 2].Char);
        Assert.Equal(E, s[0, 3].Char);
        Assert.Equal(E, s[0, 4].Char);
    }

    // ------------------------------------------------------------------
    // CSI scrolling — SU, SD
    // ------------------------------------------------------------------

    [Fact]
    public void CSI_S_SU_ScrollUp()
    {
        var t = Make(cols: 5, rows: 3);
        Write(t, "AAAAA\r\nBBBBB\r\nCCCCC");
        Write(t, "\u001B[2S");
        var s = t.GetSnapshot();
        Assert.Equal('C', s[0, 0].Char);
        Assert.Equal(E, s[1, 0].Char);
        Assert.Equal(E, s[2, 0].Char);
    }

    [Fact]
    public void CSI_T_SD_ScrollDown()
    {
        var t = Make(cols: 5, rows: 3);
        Write(t, "AAAAA\r\nBBBBB\r\nCCCCC");
        Write(t, "\u001B[2T");
        var s = t.GetSnapshot();
        Assert.Equal(E, s[0, 0].Char);
        Assert.Equal(E, s[1, 0].Char);
        Assert.Equal('A', s[2, 0].Char);
    }

    // ------------------------------------------------------------------
    // DECSTBM — scrolling region
    // ------------------------------------------------------------------

    [Fact]
    public void CSI_r_DECSTBM_SetScrollRegion_HomesCursor()
    {
        var t = Make(rows: 10);
        Write(t, "\u001B[5;5H");
        Write(t, "\u001B[3;7r");
        Assert.Equal(0, t.CursorRow);
        Assert.Equal(0, t.CursorCol);
    }

    [Fact]
    public void CSI_r_DECSTBM_LFScrollsWithinRegion()
    {
        var t = Make(cols: 5, rows: 6);
        Write(t, "00000\r\n11111\r\n22222\r\n33333\r\n44444\r\n55555");
        // Set scroll region rows 2-4 (1-based = rows 1-3 zero-based), cursor homes
        Write(t, "\u001B[2;4r");
        // Move to row 4 (1-based) = row 3 (0-based) = bottom of region
        Write(t, "\u001B[4;1H");
        // LF scrolls region
        Write(t, "\n");
        var s = t.GetSnapshot();
        Assert.Equal('0', s[0, 0].Char);   // row 0 unchanged
        Assert.Equal('2', s[1, 0].Char);   // 11111 scrolled out, 22222 up
        Assert.Equal('3', s[2, 0].Char);
        Assert.Equal(E, s[3, 0].Char);     // new blank line
        Assert.Equal('4', s[4, 0].Char);   // below region unchanged
        Assert.Equal('5', s[5, 0].Char);
    }

    [Fact]
    public void CSI_r_DECSTBM_Reset_ToFullScreen()
    {
        var t = Make(cols: 5, rows: 5);
        Write(t, "\u001B[2;4r");
        Write(t, "\u001B[r");   // reset (no params = full screen)
        Write(t, "AAAAA\r\nBBBBB\r\nCCCCC\r\nDDDDD\r\nEEEEE");
        Write(t, "\n");
        var s = t.GetSnapshot();
        Assert.Equal('B', s[0, 0].Char);
    }

    // ------------------------------------------------------------------
    // CSI misc — REP, TBC, DSR, DECSTR
    // ------------------------------------------------------------------

    [Fact]
    public void CSI_b_REP_RepeatLastChar()
    {
        var t = Make(cols: 10, rows: 1);
        Write(t, "A\u001B[4b");
        var s = t.GetSnapshot();
        Assert.Equal('A', s[0, 0].Char);
        Assert.Equal('A', s[0, 1].Char);
        Assert.Equal('A', s[0, 2].Char);
        Assert.Equal('A', s[0, 3].Char);
        Assert.Equal('A', s[0, 4].Char);
        Assert.Equal(E, s[0, 5].Char);
    }

    [Fact]
    public void CSI_g_TBC_ClearCurrentTabStop()
    {
        var t = Make(cols: 20);
        Write(t, "\u001B[1;9H");   // col 8 (0-based)
        Write(t, "\u001B[0g");     // clear stop at col 8
        Write(t, "\u001B[H");
        Write(t, "\t");            // next stop after 0 is now 16 (8 removed)
        Assert.Equal(16, t.CursorCol);
    }

    [Fact]
    public void CSI_g_TBC3_ClearAllTabStops()
    {
        var t = Make(cols: 20);
        Write(t, "\u001B[3g");
        Write(t, "\u001B[H");
        Write(t, "\t");            // no stops → clamp at last col
        Assert.Equal(19, t.CursorCol);
    }

    [Fact]
    public void CSI_n_DSR_Ignored()
    {
        var t = Make();
        Write(t, "\u001B[5;10H\u001B[6n");
        Assert.Equal(4, t.CursorRow);
        Assert.Equal(9, t.CursorCol);
    }

    [Fact]
    public void CSI_ExclP_DECSTR_SoftReset()
    {
        var t = Make(cols: 5, rows: 5);
        Write(t, "\u001B[1m");
        Write(t, "\u001B[?25l");
        Write(t, "\u001B[2;4r");
        Write(t, "\u001B[!p");
        Assert.False(t.CursorVisible); // DECSTR must not override explicit ?25l — cursor stays hidden
        // Scroll region reset to full screen — LF at bottom scrolls whole screen
        Write(t, "AAAAA\r\nBBBBB\r\nCCCCC\r\nDDDDD\r\nEEEEE");
        Write(t, "\n");
        Assert.Equal('B', Char(t, 0, 0));
    }

    [Fact]
    public void CSI_t_XTWINOPS_Resize()
    {
        var t = Make(cols: 80, rows: 24);
        Write(t, "\u001B[8;10;40t");
        Assert.Equal(40, t.Cols);
        Assert.Equal(10, t.Rows);
    }

    // ------------------------------------------------------------------
    // DECSET / DECRST private modes
    // ------------------------------------------------------------------

    [Fact]
    public void DECSET_25_ShowCursor()
    {
        var t = Make();
        Write(t, "\u001B[?25l");
        Assert.False(t.CursorVisible);
        Write(t, "\u001B[?25h");
        Assert.True(t.CursorVisible);
    }

    [Fact]
    public void DECRST_25_HideCursor()
    {
        var t = Make();
        Write(t, "\u001B[?25l");
        Assert.False(t.CursorVisible);
    }

    [Fact]
    public void DECSET_47_AlternateScreen()
    {
        var t = Make(cols: 5, rows: 2);
        Write(t, "HELLO");
        Write(t, "\u001B[?47h");
        Assert.Equal(E, Char(t, 0, 0));
        Write(t, "\u001B[?47l");
        Assert.Equal('H', Char(t, 0, 0));
    }

    [Fact]
    public void DECSET_1047_AlternateScreen()
    {
        var t = Make(cols: 5, rows: 2);
        Write(t, "HELLO");
        Write(t, "\u001B[?1047h");
        Assert.True(t.IsAlternateScreen);
        Assert.Equal(E, Char(t, 0, 0)); // alt screen is cleared
        Write(t, "\u001B[?1047l");
        Assert.False(t.IsAlternateScreen);
        Assert.Equal('H', Char(t, 0, 0)); // main screen restored
    }

    [Fact]
    public void DECSET_1049_AltScreen_SaveRestoreCursor()
    {
        var t = Make(cols: 5, rows: 5);
        Write(t, "\u001B[3;4H");
        Write(t, "\u001B[?1049h");
        Assert.Equal(E, Char(t, 0, 0));
        Write(t, "\u001B[?1049l");
        Assert.Equal(2, t.CursorRow);
        Assert.Equal(3, t.CursorCol);
    }

    // ------------------------------------------------------------------
    // SGR — attributes
    // ------------------------------------------------------------------

    [Fact]
    public void SGR_0_Reset()
    {
        var t = Make();
        Write(t, "\u001B[1;3;4m");
        Write(t, "\u001B[0m");
        Assert.Equal(CellAttributes.None, t.CurrentAttributes);
        Assert.Equal(CellColor.Default, t.CurrentFg);
        Assert.Equal(CellColor.Default, t.CurrentBg);
    }

    [Fact]
    public void SGR_EmptyParams_Reset()
    {
        var t = Make();
        Write(t, "\u001B[1m\u001B[m");
        Assert.Equal(CellAttributes.None, t.CurrentAttributes);
    }

    [Fact]
    public void SGR_1_Bold()
    {
        var t = Make();
        Write(t, "\u001B[1mA");
        Assert.True(Cell(t, 0, 0).Attributes.HasFlag(CellAttributes.Bold));
    }

    [Fact]
    public void SGR_2_Dim()
    {
        var t = Make();
        Write(t, "\u001B[2mA");
        Assert.True(Cell(t, 0, 0).Attributes.HasFlag(CellAttributes.Dim));
    }

    [Fact]
    public void SGR_3_Italic()
    {
        var t = Make();
        Write(t, "\u001B[3mA");
        Assert.True(Cell(t, 0, 0).Attributes.HasFlag(CellAttributes.Italic));
    }

    [Fact]
    public void SGR_4_Underline()
    {
        var t = Make();
        Write(t, "\u001B[4mA");
        Assert.True(Cell(t, 0, 0).Attributes.HasFlag(CellAttributes.Underline));
    }

    [Fact]
    public void SGR_5_Blink()
    {
        var t = Make();
        Write(t, "\u001B[5mA");
        Assert.True(Cell(t, 0, 0).Attributes.HasFlag(CellAttributes.Blink));
    }

    [Fact]
    public void SGR_6_RapidBlink_SameAsBlink()
    {
        var t = Make();
        Write(t, "\u001B[6mA");
        Assert.True(Cell(t, 0, 0).Attributes.HasFlag(CellAttributes.Blink));
    }

    [Fact]
    public void SGR_7_Inverse()
    {
        var t = Make();
        Write(t, "\u001B[7mA");
        Assert.True(Cell(t, 0, 0).Attributes.HasFlag(CellAttributes.Inverse));
    }

    [Fact]
    public void SGR_8_Invisible()
    {
        var t = Make();
        Write(t, "\u001B[8mA");
        Assert.True(Cell(t, 0, 0).Attributes.HasFlag(CellAttributes.Invisible));
    }

    [Fact]
    public void SGR_9_Strikethrough()
    {
        var t = Make();
        Write(t, "\u001B[9mA");
        Assert.True(Cell(t, 0, 0).Attributes.HasFlag(CellAttributes.Strike));
    }

    [Fact]
    public void SGR_22_TurnOffBoldAndDim()
    {
        var t = Make();
        Write(t, "\u001B[1;2m\u001B[22m");
        Assert.False(t.CurrentAttributes.HasFlag(CellAttributes.Bold));
        Assert.False(t.CurrentAttributes.HasFlag(CellAttributes.Dim));
    }

    [Fact]
    public void SGR_23_TurnOffItalic()
    {
        var t = Make();
        Write(t, "\u001B[3;23m");
        Assert.False(t.CurrentAttributes.HasFlag(CellAttributes.Italic));
    }

    [Fact]
    public void SGR_24_TurnOffUnderline()
    {
        var t = Make();
        Write(t, "\u001B[4;24m");
        Assert.False(t.CurrentAttributes.HasFlag(CellAttributes.Underline));
    }

    [Fact]
    public void SGR_25_TurnOffBlink()
    {
        var t = Make();
        Write(t, "\u001B[5;25m");
        Assert.False(t.CurrentAttributes.HasFlag(CellAttributes.Blink));
    }

    [Fact]
    public void SGR_27_TurnOffInverse()
    {
        var t = Make();
        Write(t, "\u001B[7;27m");
        Assert.False(t.CurrentAttributes.HasFlag(CellAttributes.Inverse));
    }

    [Fact]
    public void SGR_28_TurnOffInvisible()
    {
        var t = Make();
        Write(t, "\u001B[8;28m");
        Assert.False(t.CurrentAttributes.HasFlag(CellAttributes.Invisible));
    }

    [Fact]
    public void SGR_29_TurnOffStrike()
    {
        var t = Make();
        Write(t, "\u001B[9;29m");
        Assert.False(t.CurrentAttributes.HasFlag(CellAttributes.Strike));
    }

    [Fact]
    public void SGR_30to37_StandardFgColors()
    {
        for (int n = 30; n <= 37; n++)
        {
            var t = Make();
            Write(t, $"\u001B[{n}mA");
            var fg = Cell(t, 0, 0).Fg;
            Assert.True(fg.IsPalette, $"SGR {n} should set palette");
            Assert.Equal((byte)(n - 30), fg.PaletteIndex);
        }
    }

    [Fact]
    public void SGR_39_DefaultFg()
    {
        var t = Make();
        Write(t, "\u001B[31m\u001B[39mA");
        Assert.True(Cell(t, 0, 0).Fg.IsDefault);
    }

    [Fact]
    public void SGR_40to47_StandardBgColors()
    {
        for (int n = 40; n <= 47; n++)
        {
            var t = Make();
            Write(t, $"\u001B[{n}mA");
            var bg = Cell(t, 0, 0).Bg;
            Assert.True(bg.IsPalette, $"SGR {n} should set palette");
            Assert.Equal((byte)(n - 40), bg.PaletteIndex);
        }
    }

    [Fact]
    public void SGR_49_DefaultBg()
    {
        var t = Make();
        Write(t, "\u001B[41m\u001B[49mA");
        Assert.True(Cell(t, 0, 0).Bg.IsDefault);
    }

    [Fact]
    public void SGR_90to97_BrightFgColors()
    {
        for (int n = 90; n <= 97; n++)
        {
            var t = Make();
            Write(t, $"\u001B[{n}mA");
            var fg = Cell(t, 0, 0).Fg;
            Assert.True(fg.IsPalette);
            Assert.Equal((byte)(n - 90 + 8), fg.PaletteIndex);
        }
    }

    [Fact]
    public void SGR_100to107_BrightBgColors()
    {
        for (int n = 100; n <= 107; n++)
        {
            var t = Make();
            Write(t, $"\u001B[{n}mA");
            var bg = Cell(t, 0, 0).Bg;
            Assert.True(bg.IsPalette);
            Assert.Equal((byte)(n - 100 + 8), bg.PaletteIndex);
        }
    }

    [Fact]
    public void SGR_38_5_256ColorFg()
    {
        var t = Make();
        Write(t, "\u001B[38;5;200mA");
        var fg = Cell(t, 0, 0).Fg;
        Assert.True(fg.IsPalette);
        Assert.Equal(200, fg.PaletteIndex);
    }

    [Fact]
    public void SGR_38_2_TrueColorFg()
    {
        var t = Make();
        Write(t, "\u001B[38;2;10;20;30mA");
        var fg = Cell(t, 0, 0).Fg;
        Assert.True(fg.IsRgb);
        Assert.Equal(10, fg.R);
        Assert.Equal(20, fg.G);
        Assert.Equal(30, fg.B);
    }

    [Fact]
    public void SGR_48_5_256ColorBg()
    {
        var t = Make();
        Write(t, "\u001B[48;5;100mA");
        var bg = Cell(t, 0, 0).Bg;
        Assert.True(bg.IsPalette);
        Assert.Equal(100, bg.PaletteIndex);
    }

    [Fact]
    public void SGR_48_2_TrueColorBg()
    {
        var t = Make();
        Write(t, "\u001B[48;2;255;128;0mA");
        var bg = Cell(t, 0, 0).Bg;
        Assert.True(bg.IsRgb);
        Assert.Equal(255, bg.R);
        Assert.Equal(128, bg.G);
        Assert.Equal(0, bg.B);
    }

    [Fact]
    public void SGR_MultipleParams_InOneSequence()
    {
        var t = Make();
        Write(t, "\u001B[1;3;31;42mA");
        var cell = Cell(t, 0, 0);
        Assert.True(cell.Attributes.HasFlag(CellAttributes.Bold));
        Assert.True(cell.Attributes.HasFlag(CellAttributes.Italic));
        Assert.Equal(1, cell.Fg.PaletteIndex);
        Assert.Equal(2, cell.Bg.PaletteIndex);
    }

    // ------------------------------------------------------------------
    // OSC / DCS — ignored gracefully
    // ------------------------------------------------------------------

    [Fact]
    public void OSC_SetTitle_Ignored()
    {
        var t = Make();
        Write(t, "\u001B]0;My Title\a");
        Assert.Equal(0, t.CursorRow);
        Assert.Equal(0, t.CursorCol);
    }

    [Fact]
    public void OSC_TerminatedByST_Ignored()
    {
        var t = Make();
        Write(t, "\u001B]2;title\u001B\\");
        Assert.Equal(0, t.CursorCol);
    }

    [Fact]
    public void DCS_Passthrough_Ignored()
    {
        var t = Make();
        Write(t, "\u001BPsome data\u001B\\A");
        Assert.Equal('A', Char(t, 0, 0));
    }

    // ------------------------------------------------------------------
    // Split / chunked sequences
    // ------------------------------------------------------------------

    [Fact]
    public void SplitEscapeSequence_WorksAcrossChunks()
    {
        var t = Make();
        t.Write(new byte[] { 0x1B, (byte)'[', (byte)'5', (byte)';' }.AsSpan());
        t.Write(new byte[] { (byte)'3', (byte)'H' }.AsSpan());
        Assert.Equal(4, t.CursorRow);
        Assert.Equal(2, t.CursorCol);
    }

    [Fact]
    public void SplitUtf8_WorksAcrossChunks()
    {
        var t = Make();
        t.Write(new byte[] { 0xC3 }.AsSpan());
        t.Write(new byte[] { 0xA9 }.AsSpan());
        Assert.Equal('\u00E9', Char(t, 0, 0));
    }

    // ------------------------------------------------------------------
    // UTF-8 and wide characters
    // ------------------------------------------------------------------

    [Fact]
    public void UTF8_TwoByteChar()
    {
        var t = Make();
        t.Write(new byte[] { 0xC3, 0xA9 }.AsSpan());  // é
        Assert.Equal('\u00E9', Char(t, 0, 0));
        Assert.Equal(1, t.CursorCol);
    }

    [Fact]
    public void UTF8_ThreeByteChar()
    {
        var t = Make();
        t.Write(new byte[] { 0xE2, 0x98, 0x83 }.AsSpan());  // ☃ U+2603
        Assert.Equal('\u2603', Char(t, 0, 0));
        Assert.Equal(1, t.CursorCol);
    }

    [Fact]
    public void Wide_CJK_OccupiesTwoColumns()
    {
        var t = Make();
        t.Write(new byte[] { 0xE4, 0xB8, 0xAD }.AsSpan());  // 中 U+4E2D
        Assert.Equal('\u4E2D', Char(t, 0, 0));
        Assert.True(t.GetSnapshot()[0, 1].IsWideContinuation);
        Assert.Equal(2, t.CursorCol);
    }

    // ------------------------------------------------------------------
    // Save/restore cursor — CSI s / CSI u
    // ------------------------------------------------------------------

    [Fact]
    public void CSI_s_SaveCursor_CSI_u_RestoreCursor()
    {
        var t = Make();
        Write(t, "\u001B[8;12H");
        Write(t, "\u001B[s");
        Write(t, "\u001B[1;1H");
        Write(t, "\u001B[u");
        Assert.Equal(7, t.CursorRow);
        Assert.Equal(11, t.CursorCol);
    }

    // ------------------------------------------------------------------
    // Auto-wrap and scrollback
    // ------------------------------------------------------------------

    [Fact]
    public void AutoWrap_WritePastLastColumn_WrapsToNextLine()
    {
        var t = Make(cols: 5, rows: 3);
        Write(t, "ABCDEFGH");
        Assert.Equal('A', Char(t, 0, 0));
        Assert.Equal('E', Char(t, 0, 4));
        Assert.Equal('F', Char(t, 1, 0));
        Assert.Equal('G', Char(t, 1, 1));
        Assert.Equal('H', Char(t, 1, 2));
    }

    [Fact]
    public void Scrollback_PushesRowsOnLF()
    {
        var t = Make(cols: 5, rows: 2);
        Write(t, "AAAAA\r\nBBBBB\r\nCCCCC");
        var sb = t.GetScrollback();
        Assert.True(sb.Length >= 1);
        Assert.Equal('A', sb[0][0].Char);
    }

    [Fact]
    public void CSI_3J_ClearsScrollback_WithoutClearingVisibleScreen()
    {
        var t = Make(cols: 5, rows: 2);
        Write(t, "AAAAA\r\nBBBBB\r\nCCCCC");

        var before = t.GetScreenText();
        Assert.NotEmpty(t.GetScrollback());

        Write(t, "\u001B[3J");

        Assert.Empty(t.GetScrollback());
        Assert.Equal(before, t.GetScreenText());
    }

    // ------------------------------------------------------------------
    // Resize
    // ------------------------------------------------------------------

    [Fact]
    public void Resize_PreservesContent()
    {
        var t = Make(cols: 5, rows: 3);
        Write(t, "ABCDE\r\nFGHIJ");
        t.Resize(10, 5);
        Assert.Equal('A', Char(t, 0, 0));
        Assert.Equal('F', Char(t, 1, 0));
        Assert.Equal(10, t.Cols);
        Assert.Equal(5, t.Rows);
    }

    [Fact]
    public void Resize_TabStops_ExtendedWithDefaults()
    {
        var t = Make(cols: 10, rows: 5);
        t.Resize(20, 5);
        Write(t, "\u001B[H\t");
        Assert.Equal(8, t.CursorCol);
        Write(t, "\t");
        Assert.Equal(16, t.CursorCol);
    }

    [Fact]
    public void DECSET_MultiplePrivateModes_AppliesEveryMode()
    {
        var t = Make(cols: 5, rows: 5);
        Write(t, "\u001B[?25l");
        Assert.False(t.CursorVisible);

        Write(t, "\u001B[?1049;25h");

        Assert.True(t.IsAlternateScreen);
        Assert.True(t.CursorVisible);
    }

    [Fact]
    public void DECSCUSR_SetsCursorShape()
    {
        var t = Make();
        Write(t, "\u001B[5 q");
        Assert.Equal(5, t.CursorShape);
    }

    [Fact]
    public void DECSET_2026_TracksSynchronizedOutput()
    {
        var t = Make();
        Write(t, "\u001B[?2026h");
        Assert.True(t.SyncOutputActive);

        Write(t, "\u001B[?2026l");
        Assert.False(t.SyncOutputActive);
    }

    // ------------------------------------------------------------------
    // Parser robustness — malformed input must not crash
    // ------------------------------------------------------------------

    [Fact]
    public void CSI_ExcessiveParams_DoesNotCrash()
    {
        // 20 params exceeds the 16-element _params array.
        // Parser must clamp gracefully, not throw IndexOutOfRangeException.
        var t = Make();
        Write(t, "\u001B[1;2;3;4;5;6;7;8;9;10;11;12;13;14;15;16;17;18;19;20m");
        // No assertion on result — the point is it must not crash.
    }

    [Fact]
    public void CSI_ExcessiveParams_SGR_ProcessesValidPrefix()
    {
        // First 16 params should still be processed correctly
        var t = Make();
        Write(t, "\u001B[1;2;3;4;5;6;7;8;9;10;11;12;13;14;15;16;17;18;19;20mA");
        // Bold (param 1) should have been applied to the character
        Assert.True(t.GetSnapshot()[0, 0].Attributes.HasFlag(CellAttributes.Bold));
    }

    [Fact]
    public void OSC_TerminatedByEscBackslash_ReturnsToGround()
    {
        // ESC ] 0 ; t i t l e ESC \  — then normal text should print
        var t = Make();
        Write(t, "\u001B]0;hello\u001B\\A");
        Assert.Equal('A', Char(t, 0, 0));
    }

    [Fact]
    public void DCS_TerminatedByEscBackslash_ReturnsToGround()
    {
        // ESC P ... ESC \  — then normal text should print
        var t = Make();
        Write(t, "\u001BPsome dcs data\u001B\\B");
        Assert.Equal('B', Char(t, 0, 0));
    }
}
