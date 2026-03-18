using System.Text;

/// <summary>Visual demos for every major ANSI/VT escape sequence category.</summary>
public record SequenceDemo(string Name, string Category, string Description, string Sequence);

public static class DemoBuilder
{
    private const string E = "\u001B";       // ESC
    private const string C = "\u001B[";      // CSI
    private const string R = "\u001B[0m";    // SGR reset

    public static List<SequenceDemo> Build()
    {
        var list = new List<SequenceDemo>();
        void Add(string name, string cat, string desc, string seq) =>
            list.Add(new SequenceDemo(name, cat, desc, seq));

        // ── C0 Control Characters ──────────────────────────────────────────

        Add("NUL / BEL — Ignored", "C0 Controls",
            "0x00 NUL and 0x07 BEL are silently ignored; surrounding text is preserved",
            "NUL (0x00) and BEL (0x07) are silently ignored:\r\n\r\n" +
            "  Write 'A' + NUL + 'B'   =>  A\0B\r\n" +
            "  Write 'A' + BEL + 'B'   =>  A\aB\r\n\r\n" +
            "(Both lines should show 'AB' with no gaps or artifacts)");

        Add("BS — Backspace", "C0 Controls",
            "0x08 BS moves cursor one column left; writing overwrites",
            "Backspace moves cursor left (allows overwrite):\r\n\r\n" +
            "  'ABCD' + BS + BS + 'XY'  =>  ABXY\r\n\r\n" +
            "  ABCD\b\bXY\r\n\r\n" +
            "  BS at column 0 stays at 0:\r\n" +
            "  \b\b[cursor stays at col 0]");

        Add("HT — Tab Stops (every 8)", "C0 Controls",
            "0x09 HT advances cursor to next tab stop (default: every 8 columns)",
            "Tab ruler:\r\n" +
            "|       |       |       |       |       |       |\r\n" +
            "0       8       16      24      32      40      48\r\n\r\n" +
            "Two tabs from col 0:\r\n" +
            "\t|\t|\t|  (should land at 8, 16, 24)\r\n\r\n" +
            "Tab from mid-col (col 3):\r\n" +
            "ABC\t|  (should land at 8)");

        Add("CR — Carriage Return", "C0 Controls",
            "0x0D CR resets cursor to column 0 — enables overwriting from start of line",
            "CR resets cursor to column 0:\r\n\r\n" +
            "  'HELLO WORLD' + CR + '.....'  =>  ..... WORLD\r\n\r\n" +
            "  HELLO WORLD\r.....\r\n\r\n" +
            "  (first 5 chars overwritten with dots)");

        Add("LF / VT / FF — Line Feed variants", "C0 Controls",
            "LF(0x0A), VT(0x0B), FF(0x0C) all move cursor down one row",
            "All three move cursor down:\r\n\r\n" +
            "Line 0 — after start\nLine 1 — after LF (0x0A)\x0bLine 2 — after VT (0x0B)\x0cLine 3 — after FF (0x0C)\r\n");

        // ── LF Scrolling ───────────────────────────────────────────────────

        Add("LF Scrolling", "Scrolling",
            "LF at the bottom row scrolls all content up; bottom row becomes blank",
            "Fill rows then scroll:\r\n" +
            "Row A\r\nRow B\r\nRow C\r\nRow D\r\nRow E\r\nRow F\r\nRow G\r\nRow H\r\n" +
            "Row I\r\nRow J\r\nRow K\r\nRow L\r\nRow M\r\nRow N\r\nRow O\r\nRow P\r\n" +
            "Row Q\r\nRow R\r\nRow S (now scrolled — Row A should be gone)\r\n");

        // ── ESC Sequences ─────────────────────────────────────────────────

        Add("ESC D — Index (IND)", "ESC Sequences",
            "ESC D moves cursor down; at bottom row it scrolls content up",
            $"ESC D (Index) moves cursor down:\r\n\r\n" +
            $"Line at row 0\r\n" +
            $"{C}3;1H" +
            $"Before IND at row 3{E}D" +
            $"After IND at row 4\r\n\r\n" +
            $"At bottom (scroll test):\r\n" +
            $"Alpha\r\nBeta\r\nGamma\r\nDelta\r\nEpsilon\r\nZeta{E}D" +
            $"(scroll happened)");

        Add("ESC E — Next Line (NEL)", "ESC Sequences",
            "ESC E moves cursor to start (col 0) of next line — like CR+LF",
            $"ESC E (Next Line) = move to col 0 of next row:\r\n\r\n" +
            $"Line 1 — text here{E}E" +
            $"Line 2 — at col 0{E}E" +
            $"Line 3 — at col 0\r\n");

        Add("ESC M — Reverse Index (RI)", "ESC Sequences",
            "ESC M moves cursor up; at top row it inserts a blank line (scrolls down)",
            $"ESC M at row 2 moves up:\r\n\r\n" +
            $"Row 0\r\nRow 1\r\nRow 2{C}3;1H{E}M" +
            $"(moved up to row 1)\r\n\r\n" +
            $"ESC M at top inserts blank line:\r\n" +
            $"{C}7;1H" +
            $"aaaa\r\nbbbb\r\ncccc{C}8;1H{E}M" +
            $"(blank inserted above 'aaaa')");

        Add("ESC 7/8 — Save/Restore Cursor (DECSC/DECRC)", "ESC Sequences",
            "ESC 7 saves cursor position and attributes; ESC 8 restores",
            $"Save cursor at row 4 col 20, move away, restore:\r\n\r\n" +
            $"{C}4;20H" +
            $"<< SAVED HERE >>{E}7" +
            $"{C}2;1H" +
            $"(moved to row 2 col 1){E}8" +
            $"<< RESTORED >>\r\n\r\n" +
            $"Cursor should be just after '<< RESTORED >>'");

        Add("ESC c — Full Reset (RIS)", "ESC Sequences",
            "ESC c resets terminal: clears screen, homes cursor, default attributes",
            $"Text before reset:\r\n" +
            $"{C}31;1mRed bold text\r\n{R}" +
            $"{C}5;10HCursor displaced here\r\n" +
            $"Row 3\r\nRow 4\r\n" +
            $"Resetting now...{E}c" +
            $"After RIS: clean screen, cursor at (0,0), default colors");

        // ── Cursor Movement ────────────────────────────────────────────────

        Add("CUU/CUD/CUF/CUB — Arrow Movement", "Cursor Movement",
            "ESC[nA up, ESC[nB down, ESC[nC right, ESC[nD left",
            $"Cursor movement from center:\r\n\r\n" +
            $"{C}10;1H" +
            $"  Baseline row 10{C}3A" +
            $"  UP 3 (row 7){C}6B" +
            $"  DOWN 6 (row 13){C}3A" +
            $"  UP 3 again (row 10){C}30C" +
            $"RIGHT 30{C}15D" +
            $"LEFT 15");

        Add("CUP — Absolute Position (row;col)", "Cursor Movement",
            "ESC[row;colH positions cursor absolutely (1-based). ESC[H = home",
            $"Text placed at corners and center:\r\n" +
            $"{C}2;2H{C}1mTOP-LEFT{R}" +
            $"{C}2;65H{C}1mTOP-RIGHT{R}" +
            $"{C}12;35H{C}1mCENTER{R}" +
            $"{C}20;2H{C}1mBOT-LEFT{R}" +
            $"{C}20;65H{C}1mBOT-RIGHT{R}" +
            $"{C}H(home)");

        Add("CHA/VPA — Column/Row Absolute", "Cursor Movement",
            "ESC[nG sets column (CHA, 1-based); ESC[nd sets row (VPA, 1-based)",
            $"CHA — set column absolute:\r\n\r\n" +
            $">{C}20GCOL-20" +
            $"{C}40GCOL-40" +
            $"{C}60GCOL-60\r\n\r\n" +
            $"VPA — set row absolute:\r\n" +
            $"{C}12d" +
            $"   Row 12 via VPA" +
            $"{C}14d" +
            $"   Row 14 via VPA");

        Add("CNL/CPL — Next/Prev Line", "Cursor Movement",
            "ESC[nE = start of line+n (CNL); ESC[nF = start of line-n (CPL)",
            $"Start at row 8:\r\n\r\n" +
            $"{C}8;1H" +
            $"At row 8{C}3E" +
            $"CNL 3 => row 11, col 0{C}4F" +
            $"CPL 4 => row 7, col 0");

        Add("CHT/CBT — Horizontal Tab Forward/Back", "Cursor Movement",
            "ESC[nI = n tab stops forward (CHT); ESC[nZ = n tab stops back (CBT)",
            $"Tab ruler:\r\n" +
            $"|       |       |       |       |\r\n" +
            $"0       8       16      24      32\r\n\r\n" +
            $"CHT 2 from col 0 => col 16:\r\n" +
            $"[{C}2I] at col 16\r\n\r\n" +
            $"CBT 2 from col 25 => col 8:\r\n" +
            $"{C}6;25H" +
            $"[{C}2Z] at col 8");

        Add("HPR/VPR/HPA — Relative+Absolute variants", "Cursor Movement",
            "ESC[na HPR = right n; ESC[ne VPR = down n; ESC[n` HPA = set col",
            $"HPR (right), VPR (down), HPA (set col):\r\n\r\n" +
            $">{C}10a<  HPR 10 = col 12\r\n\r\n" +
            $"{C}5;1H" +
            $"Row 5{C}3e" +
            $"VPR 3 => row 8\r\n\r\n" +
            $"{C}10;1H" +
            $">{C}20`< HPA 20 = col 20");

        Add("CSI s/u — Save/Restore Cursor", "Cursor Movement",
            "ESC[s saves cursor; ESC[u restores (same as ESC 7/8)",
            $"CSI s/u save+restore:\r\n\r\n" +
            $"{C}4;18H" +
            $"<< SAVED >>{C}s" +
            $"{C}2;1H" +
            $"(moved away){C}u" +
            $"<< RESTORED >>");

        // ── Erase Sequences ────────────────────────────────────────────────

        Add("EL — Erase Line (0/1/2)", "Erase",
            "ESC[K erase to EOL; ESC[1K erase to BOL; ESC[2K erase whole line",
            $"Original:  AAAA BBBB CCCC DDDD EEEE\r\n" +
            $"Original:  AAAA BBBB CCCC DDDD EEEE\r\n" +
            $"Original:  AAAA BBBB CCCC DDDD EEEE\r\n\r\n" +
            $"EL0 (erase to end):   AAAA BBBB CCCC DDDD EEEE{C}5;12H{C}K\r\n" +
            $"EL1 (erase to start): AAAA BBBB CCCC DDDD EEEE{C}6;12H{C}1K\r\n" +
            $"EL2 (whole line):     AAAA BBBB CCCC DDDD EEEE{C}7;1H{C}2K");

        Add("ECH — Erase N Characters", "Erase",
            "ESC[nX erases n chars at cursor position without moving cursor",
            $"ECH erases chars in-place (no shift):\r\n\r\n" +
            $"AAAA|BBBBB|CCCC\r\n" +
            $"AAAA|BBBBB|CCCC{C}3;6H{C}5X\r\n\r\n" +
            $"(5 B's replaced by spaces, CCCC stays in place)");

        Add("ED — Erase Display (0/1/2)", "Erase",
            "ESC[J erase to end; ESC[1J erase to start; ESC[2J erase all",
            $"ED0 — erase from cursor to end of screen:\r\n" +
            $"Row 1: AAAA BBBB CCCC\r\n" +
            $"Row 2: AAAA BBBB CCCC  <- cursor here, erase forward\r\n" +
            $"Row 3: AAAA BBBB CCCC\r\n" +
            $"Row 4: AAAA BBBB CCCC\r\n" +
            $"{C}3;12H{C}J\r\n" +
            $"(rows 2 partial + 3,4 should be blank)");

        // ── Insert / Delete ────────────────────────────────────────────────

        Add("IL/DL — Insert/Delete Lines", "Insert/Delete",
            "ESC[nL inserts n blank lines; ESC[nM deletes n lines",
            $"IL — insert 2 lines at row 4:\r\n" +
            $"Row 0 (fixed)\r\nRow 1 (fixed)\r\nRow 2 (fixed)\r\n" +
            $"Row 3 — pushed down\r\nRow 4 — pushed down\r\n" +
            $"{C}4;1H{C}2L" +
            $"<inserted line 1>\r\n<inserted line 2>\r\n\r\n" +
            $"DL — delete 2 lines at row 9:\r\n" +
            $"Keep A\r\nKeep B\r\nDELETED\r\nDELETED\r\nPulled up C\r\n" +
            $"{C}12;1H{C}2M");

        Add("ICH/DCH — Insert/Delete Characters", "Insert/Delete",
            "ESC[n@ inserts n blank chars; ESC[nP deletes n chars",
            $"ICH — insert 3 blank chars at col 5:\r\n\r\n" +
            $"AAAA|BBBB|CCCC\r\n" +
            $"AAAA|BBBB|CCCC{C}3;6H{C}3@\r\n\r\n" +
            $"(gap inserted before B's, C's may shift off edge)\r\n\r\n" +
            $"DCH — delete 3 chars at col 5:\r\n\r\n" +
            $"AAAA|BBBB|CCCC\r\n" +
            $"AAAA|BBBB|CCCC{C}7;6H{C}3P\r\n\r\n" +
            $"(3 chars deleted at col 5, rest pulls left)");

        Add("REP — Repeat Last Character", "Insert/Delete",
            "ESC[nb repeats the last written character n times",
            $"REP repeats the last output character:\r\n\r\n" +
            $"X{C}15b   <- 'X' repeated 15 times\r\n" +
            $"{C}31m*{R}{C}25b   <- red '*' repeated 25 times\r\n" +
            $"{C}32m#{R}{C}35b   <- green '#' repeated 35 times\r\n" +
            $"{C}33m={R}{C}60b   <- yellow '=' repeated 60 times\r\n");

        Add("SU/SD — Scroll Up/Down", "Scrolling",
            "ESC[nS scrolls up n lines; ESC[nT scrolls down n lines",
            $"Before scroll (8 rows of content):\r\n" +
            $"Line 1\r\nLine 2\r\nLine 3\r\nLine 4\r\nLine 5\r\nLine 6\r\nLine 7\r\nLine 8\r\n" +
            $"--- SU 3 (scroll up 3): ---\r\n" +
            $"{C}3S" +
            $"(Line 1-3 gone, 3 blanks at bottom)\r\n\r\n" +
            $"--- SD 2 (scroll down 2): ---\r\n" +
            $"{C}2T" +
            $"(2 blank lines inserted at top)");

        Add("DECSTBM — Scroll Region", "Scrolling",
            "ESC[top;botr limits scrolling to rows top..bot; LF/SU only scroll within it",
            $"Fixed row 0 (outside region)\r\n" +
            $"Fixed row 1 (outside region)\r\n" +
            $"--- scroll region rows 3-8 ---\r\n" +
            $"Scrollable A\r\nScrollable B\r\nScrollable C\r\nScrollable D\r\nScrollable E\r\n" +
            $"--- scroll region end ---\r\n" +
            $"Fixed row 9 (outside region)\r\n" +
            $"{C}3;8r" +
            $"{C}3;1H" +
            $"NEW-A\r\nNEW-B\r\nNEW-C\r\nNEW-D\r\nNEW-E\r\nNEW-F\r\nNEW-G\r\n" +
            $"{C}r");

        // ── Screen Modes ───────────────────────────────────────────────────

        Add("DECSET ?25 — Show/Hide Cursor", "Screen Modes",
            "ESC[?25h shows cursor; ESC[?25l hides it",
            $"Cursor visibility:\r\n\r\n" +
            $"Cursor is visible here (default)\r\n" +
            $"{C}?25l" +
            $"Cursor hidden (ESC[?25l)\r\n" +
            $"{C}?25h" +
            $"Cursor shown again (ESC[?25h)\r\n");

        Add("DECSET ?1049 — Alternate Screen", "Screen Modes",
            "ESC[?1049h saves cursor + switches to alt screen; ESC[?1049l restores",
            $"=== MAIN SCREEN ===\r\n" +
            $"Main row 1\r\nMain row 2\r\nMain row 3\r\n" +
            $"Switching to alt screen...\r\n" +
            $"{C}?1049h" +
            $"{C}31m=== ALTERNATE SCREEN ==={R}\r\n" +
            $"Alt row 1\r\nAlt row 2\r\nAlt row 3\r\n" +
            $"(main screen content is hidden)\r\n" +
            $"Returning to main...\r\n" +
            $"{C}?1049l");

        // ── SGR Colors ─────────────────────────────────────────────────────

        Add("SGR 30-37 — Standard FG Colors", "SGR Colors",
            "ESC[30m..ESC[37m set standard foreground colors (8 colors)",
            $"Standard foreground colors:\r\n\r\n" +
            string.Concat(Enumerable.Range(30, 8).Select(i =>
                $"  {C}{i}m  SGR {i}  Sample text: The quick brown fox  {R}\r\n")));

        Add("SGR 40-47 — Standard BG Colors", "SGR Colors",
            "ESC[40m..ESC[47m set standard background colors (8 colors)",
            $"Standard background colors:\r\n\r\n" +
            string.Concat(Enumerable.Range(40, 8).Select(i =>
                $"  {C}{i}m  SGR {i}  Sample text: The quick brown fox  {R}\r\n")));

        Add("SGR 90-97 — Bright FG Colors", "SGR Colors",
            "ESC[90m..ESC[97m set bright/high-intensity foreground colors",
            $"Bright foreground colors:\r\n\r\n" +
            string.Concat(Enumerable.Range(90, 8).Select(i =>
                $"  {C}{i}m  SGR {i}  Sample text: The quick brown fox  {R}\r\n")));

        Add("SGR 100-107 — Bright BG Colors", "SGR Colors",
            "ESC[100m..ESC[107m set bright/high-intensity background colors",
            $"Bright background colors:\r\n\r\n" +
            string.Concat(Enumerable.Range(100, 8).Select(i =>
                $"  {C}{i}m  SGR {i}  Sample text: The quick brown fox  {R}\r\n")));

        Add("SGR: All 16 Colors — FG vs BG Matrix", "SGR Colors",
            "Side-by-side comparison of all 16 colors as both foreground and background",
            $"   FG (text color)          BG (background color)\r\n\r\n" +
            string.Concat(Enumerable.Range(0, 8).Select(i =>
                $"  {C}{30 + i}m {30+i} sample text {R}     " +
                $"  {C}{40 + i}m {40+i} sample text {R}     " +
                $"  {C}{90 + i}m {90+i} bright    {R}     " +
                $"  {C}{100 + i}m {100+i} bright   {R}\r\n")));

        Add("SGR 38;5 — 256-Color FG Palette", "SGR Colors",
            "ESC[38;5;nm selects foreground from the 256-color palette",
            $"256-color palette (fg):\r\n\r\n" +
            string.Concat(Enumerable.Range(0, 256).Select(i =>
                $"{C}38;5;{i}m{i,4}{R}" + (i % 16 == 15 ? "\r\n" : ""))) + "\r\n");

        Add("SGR 48;5 — 256-Color BG Palette", "SGR Colors",
            "ESC[48;5;nm selects background from the 256-color palette",
            $"256-color palette (bg, each cell = 3 spaces):\r\n\r\n" +
            string.Concat(Enumerable.Range(0, 256).Select(i =>
                $"{C}48;5;{i}m   {R}" + (i % 32 == 31 ? "\r\n" : ""))) + "\r\n");

        Add("SGR 38;2 / 48;2 — True Color (24-bit RGB)", "SGR Colors",
            "ESC[38;2;r;g;bm (fg) and ESC[48;2;r;g;bm (bg) — full 24-bit color",
            TrueColorDemo());

        // ── SGR Attributes ──────────────────────────────────────────────────

        Add("SGR 1 — Bold", "SGR Attributes",
            "ESC[1m bold; ESC[22m turns off bold (and dim)",
            $"Bold attribute:\r\n\r\n" +
            $"Normal:  The quick brown fox jumps over the lazy dog\r\n" +
            $"{C}1mBold:    The quick brown fox jumps over the lazy dog{R}\r\n\r\n" +
            $"Mixed: normal {C}1mbold{R} normal {C}1mbold{R} normal\r\n\r\n" +
            $"Turn off: {C}1mBOLD{C}22m not-bold{R}");

        Add("SGR 2 — Dim / Faint", "SGR Attributes",
            "ESC[2m dim/faint; ESC[22m turns off dim (and bold)",
            $"Dim attribute:\r\n\r\n" +
            $"Normal:  The quick brown fox\r\n" +
            $"{C}2mDim:     The quick brown fox{R}\r\n\r\n" +
            $"Mixed: normal {C}2mdim{R} normal\r\n\r\n" +
            $"Turn off: {C}2mDIM{C}22m not-dim{R}");

        Add("SGR 3 — Italic", "SGR Attributes",
            "ESC[3m italic; ESC[23m turns it off (rendering depends on font/terminal)",
            $"Italic attribute:\r\n\r\n" +
            $"Normal:  The quick brown fox\r\n" +
            $"{C}3mItalic:  The quick brown fox{R}\r\n\r\n" +
            $"Mixed: normal {C}3mitalic{R} normal\r\n\r\n" +
            $"Turn off: {C}3mITALIC{C}23m not-italic{R}");

        Add("SGR 4 — Underline", "SGR Attributes",
            "ESC[4m underline; ESC[24m turns it off",
            $"Underline attribute:\r\n\r\n" +
            $"Normal:      The quick brown fox\r\n" +
            $"{C}4mUnderlined: The quick brown fox{R}\r\n\r\n" +
            $"Mixed: normal {C}4munderlined{R} normal\r\n\r\n" +
            $"Turn off: {C}4mUNDER{C}24m not-under{R}");

        Add("SGR 5/6 — Blink / Rapid Blink", "SGR Attributes",
            "ESC[5m slow blink; ESC[6m rapid blink; ESC[25m turns both off",
            $"Blink attributes (animation depends on terminal):\r\n\r\n" +
            $"Normal:      The quick brown fox\r\n" +
            $"{C}5mSlow blink: The quick brown fox{R}\r\n" +
            $"{C}6mRapid blink:The quick brown fox{R}\r\n\r\n" +
            $"Turn off: {C}5mBLINK{C}25m not-blink{R}");

        Add("SGR 7 — Inverse (Reverse Video)", "SGR Attributes",
            "ESC[7m swaps fg and bg colors; ESC[27m turns it off",
            $"Inverse / reverse video:\r\n\r\n" +
            $"Normal:   The quick brown fox\r\n" +
            $"{C}7mInverse:  The quick brown fox{R}\r\n\r\n" +
            $"With color: {C}31mRed FG{R}  vs  {C}31;7mRed inverted{R}\r\n" +
            $"With color: {C}44mBlue BG{R}  vs  {C}44;7mBlue inverted{R}\r\n\r\n" +
            $"Turn off: {C}7mINVERSE{C}27m not-inverse{R}");

        Add("SGR 8 — Invisible", "SGR Attributes",
            "ESC[8m invisible (text same color as bg); ESC[28m turns it off",
            $"Invisible text (space is occupied, text hidden):\r\n\r\n" +
            $"Normal visible text\r\n" +
            $"[{C}8mHIDDEN TEXT HERE{R}] <- hidden between brackets\r\n\r\n" +
            $"Mixed: visible [{C}8minvisible{R}] visible\r\n\r\n" +
            $"Turn off: {C}8mHIDDEN{C}28m visible{R}");

        Add("SGR 9 — Strikethrough", "SGR Attributes",
            "ESC[9m strikethrough; ESC[29m turns it off",
            $"Strikethrough attribute:\r\n\r\n" +
            $"Normal:         The quick brown fox\r\n" +
            $"{C}9mStrikethrough:  The quick brown fox{R}\r\n\r\n" +
            $"Mixed: normal {C}9mstruck{R} normal\r\n\r\n" +
            $"Turn off: {C}9mSTRIKE{C}29m not-strike{R}");

        Add("SGR — Combined Attributes", "SGR Attributes",
            "Multiple SGR params in one sequence: ESC[1;3;4;31;42m",
            $"Multiple attributes combined:\r\n\r\n" +
            $"{C}1;3;4;31;42m Bold+Italic+Underline+RedFG+GreenBG {R}\r\n\r\n" +
            $"{C}1;33mBold Yellow{R}     " +
            $"{C}3;36mItalic Cyan{R}     " +
            $"{C}4;35mUnderline Magenta{R}\r\n" +
            $"{C}7;32mInverse Green{R}   " +
            $"{C}1;4;31mBold+Under+Red{R}  " +
            $"{C}2;3;34mDim+Italic+Blue{R}\r\n\r\n" +
            $"Selective turn-off:\r\n" +
            $"{C}1;2;3;4;9mAll on{C}22m -bold/dim{C}23m -italic{C}24m -under{C}29m -strike{R}");

        Add("SGR 39/49 — Default FG/BG Colors", "SGR Attributes",
            "ESC[39m resets fg to default; ESC[49m resets bg to default",
            $"Default color reset:\r\n\r\n" +
            $"{C}31mRed FG{R}  <- ESC[0m full reset\r\n" +
            $"{C}31mRed FG{C}39m Default FG{R}  <- ESC[39m fg only\r\n\r\n" +
            $"{C}41mRed BG{R}  <- ESC[0m full reset\r\n" +
            $"{C}41mRed BG{C}49m Default BG{R}  <- ESC[49m bg only\r\n\r\n" +
            $"{C}31;41mRed+Red{C}39m FG-reset{C}49m BG-reset{R}");

        // ── Unicode / Wide Characters ───────────────────────────────────────

        Add("UTF-8 — Multi-byte Characters", "Unicode",
            "Multi-byte UTF-8 sequences (2-byte, 3-byte, 4-byte) decoded and rendered",
            "UTF-8 multi-byte characters:\r\n\r\n" +
            "2-byte (Latin ext): café naïve résumé fiancée\r\n" +
            "3-byte (Symbols):   ☃ ♥ ★ ♦ ☯ ✓ ✗ ♫ ♪ ☀ ☁\r\n" +
            "3-byte (Greek):     α β γ δ ε ζ η θ ι κ λ μ ν\r\n" +
            "4-byte (Emoji):     \U0001F600 \U0001F4BB \U0001F680 \U0001F525 \U0001F3AE\r\n\r\n" +
            "Mixed ASCII + Unicode:\r\n" +
            "  [Hello: こんにちは]  [Star: ★]  [Pi: π]\r\n");

        Add("Wide Characters — CJK (2-column cells)", "Unicode",
            "CJK characters occupy 2 columns; continuation cell is marked and skipped",
            "Wide (2-column) characters:\r\n\r\n" +
            "Chinese (4 chars = 8 cols):  中文字符\r\n" +
            "Japanese (5 chars = 10 cols): 日本語テスト\r\n" +
            "Korean (3 chars = 6 cols):   한국어\r\n\r\n" +
            "Mixed narrow + wide:\r\n" +
            "  A中B文C字D符E  (narrow+wide alternating)\r\n\r\n" +
            "Alignment check (each line should have same visual width):\r\n" +
            "  |ABCDEF|  <- 6 narrow chars\r\n" +
            "  |中文中文|  <- 4 wide chars = 8 cols... (note: wider than above)\r\n" +
            "  |AB中CD|  <- 2+2+2 = 6 cols\r\n");

        // ── Tab Stops ──────────────────────────────────────────────────────

        Add("ESC H / TBC — Custom Tab Stops", "Tab Stops",
            "ESC H sets tab stop at current col; ESC[g clears one; ESC[3g clears all",
            $"Default tab stops (every 8):\r\n" +
            $"|\t|\t|\t|\t|\r\n" +
            $"0       8       16      24      32\r\n\r\n" +
            $"Set custom stops at cols 5 and 20:\r\n" +
            $"{C}4;6H{E}H" +
            $"{C}4;21H{E}H" +
            $"{C}4;1H" +
            $"\t|\t|\t|\r\n" +
            $"0    5              20\r\n\r\n" +
            $"Clear stop at col 5 (ESC[g):\r\n" +
            $"{C}7;6H{C}g" +
            $"{C}7;1H" +
            $"\t|\t|\r\n" +
            $"0                   20");

        // ── Misc / Resize ──────────────────────────────────────────────────

        Add("XTWINOPS — Terminal Resize", "Misc",
            "ESC[8;rows;colst resizes the terminal grid",
            $"Current size before resize:\r\n\r\n" +
            $"ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789abcdefghijklmnopqrstuvwxyz\r\n" +
            $"Row 2\r\nRow 3\r\nRow 4\r\nRow 5\r\n\r\n" +
            $"Resize to 40x10:\r\n" +
            $"{C}8;10;40t" +
            $"After resize (40 cols x 10 rows)\r\n" +
            $"ABCDEFGHIJKLMNOPQRSTUVWXYZ01234567\r\n" +
            $"Row 2 after resize\r\nRow 3 after resize\r\n");

        Add("OSC / DCS — Ignored Gracefully", "Misc",
            "OSC (ESC]) and DCS (ESC P) sequences are parsed and silently discarded",
            $"OSC and DCS are ignored, surrounding text preserved:\r\n\r\n" +
            $"Before OSC title:   \u001B]0;My Window Title\u0007   After OSC title\r\n" +
            $"Before OSC (ST):    \u001B]2;alt title\u001B\\   After OSC (ST)\r\n" +
            $"Before DCS:         \u001BPsome-passthrough-data\u001B\\   After DCS\r\n\r\n" +
            $"All three lines should show only the 'Before' and 'After' text.\r\n");

        Add("Split Sequences — Across Chunks", "Misc",
            "ESC sequences split across two Write() calls are handled correctly",
            $"This demo writes ESC[5;3H as two chunks:\r\n\r\n" +
            $"(The sequence is already pre-joined here since this tool uses single Write)\r\n\r\n" +
            $"But the Terminal handles it:\r\n" +
            $"{C}5;3H" +
            $"[text at row 5 col 3 via CUP]\r\n");

        return list;
    }

    private static string TrueColorDemo()
    {
        const string C = "\u001B[";
        const string R = "\u001B[0m";
        var sb = new StringBuilder();
        sb.Append("True Color (24-bit RGB) — ESC[38;2;r;g;bm / ESC[48;2;r;g;bm:\r\n\r\n");

        sb.Append("FG rainbow:  ");
        for (int i = 0; i < 66; i++)
        {
            var (r, g, b) = HsvToRgb(i / 66.0, 1.0, 1.0);
            sb.Append($"{C}38;2;{r};{g};{b}m\u2588{R}");
        }
        sb.Append("\r\n");

        sb.Append("BG rainbow:  ");
        for (int i = 0; i < 66; i++)
        {
            var (r, g, b) = HsvToRgb(i / 66.0, 1.0, 1.0);
            sb.Append($"{C}48;2;{r};{g};{b}m {R}");
        }
        sb.Append($"{R}\r\n");

        sb.Append("Red->Blue:   ");
        for (int i = 0; i < 66; i++)
        {
            int r = (int)(255 * (1 - i / 66.0));
            int bl = (int)(255 * (i / 66.0));
            sb.Append($"{C}48;2;{r};0;{bl}m {R}");
        }
        sb.Append($"{R}\r\n");

        sb.Append("Grayscale:   ");
        for (int i = 0; i < 66; i++)
        {
            int v = (int)(255 * i / 66.0);
            sb.Append($"{C}48;2;{v};{v};{v}m {R}");
        }
        sb.Append($"{R}\r\n\r\n");

        sb.Append($"  {C}38;2;255;0;0mRed (255,0,0){R}      ");
        sb.Append($"{C}38;2;0;200;0mGreen (0,200,0){R}      ");
        sb.Append($"{C}38;2;0;80;255mBlue (0,80,255){R}\r\n");
        sb.Append($"  {C}38;2;255;165;0mOrange (255,165,0){R}   ");
        sb.Append($"{C}38;2;148;0;211mViolet (148,0,211){R}   ");
        sb.Append($"{C}38;2;0;220;220mCyan (0,220,220){R}\r\n");

        return sb.ToString();
    }

    private static (int r, int g, int b) HsvToRgb(double h, double s, double v)
    {
        int hi = (int)(h * 6) % 6;
        double f = h * 6 - Math.Floor(h * 6);
        double p = v * (1 - s);
        double q = v * (1 - f * s);
        double t = v * (1 - (1 - f) * s);
        var (rd, gd, bd) = hi switch
        {
            0 => (v, t, p), 1 => (q, v, p), 2 => (p, v, t),
            3 => (p, q, v), 4 => (t, p, v), _ => (v, p, q)
        };
        return ((int)(rd * 255), (int)(gd * 255), (int)(bd * 255));
    }
}
