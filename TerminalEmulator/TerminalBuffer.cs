namespace TerminalEmulator;

/// <summary>
/// Holds the terminal grid state: two screen buffers (normal + alternate),
/// cursor position, scrollback history, and current SGR attributes.
/// All operations are synchronous and not thread-safe — callers must serialize.
/// </summary>
public sealed class TerminalBuffer
{
    private TerminalCell[,] _normal;
    private TerminalCell[,] _alternate;
    private bool _usingAlternate;

    // Scrollback: ring buffer of rows, each a TerminalCell[]
    private readonly TerminalCell[][] _scrollback;
    private int _scrollbackHead;
    private int _scrollbackCount;

    // Cursor state — two sets so we can swap with alternate screen
    private CursorState _normalCursor;
    private CursorState _alternateCursor;

    // Current SGR pen (what new chars will be written with)
    public CellColor CurrentFg = CellColor.Default;
    public CellColor CurrentBg = CellColor.Default;
    public CellAttributes CurrentAttributes = CellAttributes.None;

    // Saved cursor (DECSC/DECRC)
    private CursorState _savedCursor;

    // Scrolling region (DECSTBM) — default is full screen
    private int _scrollTop;
    private int _scrollBottom;

    // Cursor shape (DECSCUSR): 0=default, 1=blinking block, 2=steady block,
    // 3=blinking underline, 4=steady underline, 5=blinking bar, 6=steady bar
    private int _cursorShape;

    // Synchronized output mode (?2026) — true while TUI is mid-frame redraw
    private bool _syncOutputActive;

    // Bracketed-paste mode (?2004) — true while the app wants pastes wrapped in
    // ESC[200~ / ESC[201~. Tracked so a reconnect snapshot can restore it; the
    // webview gates Shift+Enter→newline and paste-wrapping on this being on.
    private bool _bracketedPasteActive;

    // Mouse reporting state, modelled exactly as xterm.js models it in
    // CoreMouseService — because the only consumer of this state is the reconnect
    // snapshot we replay INTO xterm.js, so any divergence is a bug:
    //   * protocol (?9 X10 / ?1000 VT200 / ?1002 drag / ?1003 any) is ONE value.
    //     They are mutually exclusive and last-wins, and a DECRST of ANY of them
    //     turns tracking off entirely. Do not model these as a set that accumulates
    //     — replaying "1002;1003h" when the app last asked for 1002 silently
    //     upgrades the session to any-event tracking, and dropping one member of a
    //     set on reset leaves stale modes that resurrect tracking the app disabled.
    //   * encoding (?1006 SGR) is a separate single value. ?1005 and ?1015 are
    //     legacy encodings xterm.js does not implement at all, so they are not
    //     tracked — replaying them would be a no-op that only invites confusion.
    //   * focus reporting (?1004) is genuinely independent of both.
    // Tracked for the same reason as ?2004 above: CLIs enable these once at startup
    // and never re-assert them (opentui does not re-assert even on SIGWINCH), so a
    // reconnect snapshot that resets them without restoring them silently kills
    // wheel and click reporting for the rest of the session.
    private int _mouseProtocolMode;   // 0 = tracking off, else 9 | 1000 | 1002 | 1003
    private int _mouseEncodingMode;   // 0 = default encoding, else 1006
    private bool _focusReportingActive;

    // Tab stops — true at columns where a tab stop is set
    private bool[] _tabStops;

    public int Cols { get; private set; }
    public int Rows { get; private set; }
    public int ScrollbackSize { get; }

    public int CursorCol => ActiveCursor.Col;
    public int CursorRow => ActiveCursor.Row;
    public bool CursorVisible => ActiveCursor.Visible;
    public int CursorShape => _cursorShape;
    public bool SyncOutputActive => _syncOutputActive;
    public bool BracketedPasteActive => _bracketedPasteActive;

    public int ScrollTop => _scrollTop;
    public int ScrollBottom => _scrollBottom;

    // Tracks which rows changed since last snapshot — cleared by consumer
    private bool[] _dirtyRows;

    private ref CursorState ActiveCursor =>
        ref _usingAlternate ? ref _alternateCursor : ref _normalCursor;

    private TerminalCell[,] ActiveScreen =>
        _usingAlternate ? _alternate : _normal;

    public TerminalBuffer(int cols, int rows, int scrollbackSize = 1000)
    {
        Cols = cols;
        Rows = rows;
        ScrollbackSize = scrollbackSize;

        _normal    = new TerminalCell[rows, cols];
        _alternate = new TerminalCell[rows, cols];
        _dirtyRows = new bool[rows];

        _scrollback = new TerminalCell[scrollbackSize][];
        _scrollbackHead = 0;
        _scrollbackCount = 0;

        _scrollTop    = 0;
        _scrollBottom = rows - 1;

        _tabStops = new bool[cols];
        InitDefaultTabStops();

        FillWithEmpty(_normal);
        FillWithEmpty(_alternate);
    }

    // ------------------------------------------------------------------
    // Screen / cursor operations called by AnsiParser
    // ------------------------------------------------------------------

    public void WriteChar(char ch, bool wideChar = false)
        => WriteCodepoint(ch, wideChar);

    public void WriteCodepoint(int codepoint, bool wideChar = false)
    {
        ref var cursor = ref ActiveCursor;
        if (cursor.Col >= Cols)
        {
            cursor.Col = 0;
            LineFeed(scroll: true);
        }

        var displayChar = codepoint <= char.MaxValue
            ? (char)codepoint
            : char.ConvertFromUtf32(codepoint)[0];

        var screen = ActiveScreen;
        screen[cursor.Row, cursor.Col] = new TerminalCell
        {
            Char = displayChar,
            Codepoint = codepoint,
            Fg = CurrentFg,
            Bg = CurrentBg,
            Attributes = CurrentAttributes,
        };
        MarkDirty(cursor.Row);

        if (wideChar && cursor.Col + 1 < Cols)
        {
            screen[cursor.Row, cursor.Col + 1] = new TerminalCell
            {
                Char = '\0',
                Codepoint = 0,
                Fg = CurrentFg,
                Bg = CurrentBg,
                Attributes = CurrentAttributes,
                IsWideContinuation = true,
            };
            cursor.Col += 2;
        }
        else
        {
            cursor.Col++;
        }
    }

    public void MoveCursorTo(int row, int col)
    {
        ref var cursor = ref ActiveCursor;
        cursor.Row = Math.Clamp(row, 0, Rows - 1);
        cursor.Col = Math.Clamp(col, 0, Cols - 1);
    }

    public void MoveCursorRelative(int dRow, int dCol)
    {
        ref var cursor = ref ActiveCursor;
        MoveCursorTo(cursor.Row + dRow, cursor.Col + dCol);
    }

    public void SetCursorCol(int col) => MoveCursorTo(ActiveCursor.Row, col);
    public void SetCursorRow(int row) => MoveCursorTo(row, ActiveCursor.Col);

    public void CarriageReturn()
    {
        ActiveCursor.Col = 0;
    }

    public void LineFeed(bool scroll = true)
    {
        ref var cursor = ref ActiveCursor;
        if (cursor.Row == _scrollBottom && scroll)
        {
            ScrollUp(1);
            // cursor stays at _scrollBottom (screen scrolled under it)
        }
        else if (cursor.Row < Rows - 1)
        {
            cursor.Row++;
        }
    }

    public void ReverseLineFeed()
    {
        ref var cursor = ref ActiveCursor;
        if (cursor.Row == _scrollTop)
            InsertLineAt(_scrollTop);
        else if (cursor.Row > 0)
            cursor.Row--;
    }

    public void Tab()
    {
        ref var cursor = ref ActiveCursor;
        for (int c = cursor.Col + 1; c < Cols; c++)
        {
            if (_tabStops[c])
            {
                cursor.Col = c;
                return;
            }
        }
        cursor.Col = Cols - 1;
    }

    public void BackTab()
    {
        ref var cursor = ref ActiveCursor;
        for (int c = cursor.Col - 1; c >= 0; c--)
        {
            if (_tabStops[c])
            {
                cursor.Col = c;
                return;
            }
        }
        cursor.Col = 0;
    }

    public void Backspace()
    {
        ref var cursor = ref ActiveCursor;
        if (cursor.Col > 0) cursor.Col--;
    }

    // Set scrolling region (DECSTBM). Homes cursor to (0,0) per VT spec.
    public void SetScrollRegion(int top, int bottom)
    {
        _scrollTop    = Math.Clamp(top, 0, Rows - 1);
        _scrollBottom = Math.Clamp(bottom, _scrollTop, Rows - 1);
        MoveCursorTo(0, 0);
    }

    // Set a tab stop at the current cursor column (HTS).
    public void SetTabStop()
    {
        int col = ActiveCursor.Col;
        if (col < Cols)
            _tabStops[col] = true;
    }

    // Clear tab stop(s) (TBC). mode 0 = current column, mode 3 = all.
    public void ClearTabStop(int mode)
    {
        if (mode == 0)
        {
            int col = ActiveCursor.Col;
            if (col < Cols) _tabStops[col] = false;
        }
        else if (mode == 3)
        {
            Array.Clear(_tabStops, 0, _tabStops.Length);
        }
    }

    // Erase in display
    public void EraseInDisplay(int mode)
    {
        ref var cursor = ref ActiveCursor;
        switch (mode)
        {
            case 0: // cursor to end
                EraseRow(cursor.Row, cursor.Col, Cols);
                for (int r = cursor.Row + 1; r < Rows; r++) EraseRow(r, 0, Cols);
                break;
            case 1: // beginning to cursor
                for (int r = 0; r < cursor.Row; r++) EraseRow(r, 0, Cols);
                EraseRow(cursor.Row, 0, cursor.Col + 1);
                break;
            case 2: // whole screen
                for (int r = 0; r < Rows; r++) EraseRow(r, 0, Cols);
                break;
            case 3: // scrollback only
                ClearScrollback();
                break;
        }
    }

    public void ClearScrollback()
    {
        Array.Clear(_scrollback, 0, _scrollback.Length);
        _scrollbackHead = 0;
        _scrollbackCount = 0;
    }

    // Erase in line
    public void EraseInLine(int mode)
    {
        ref var cursor = ref ActiveCursor;
        switch (mode)
        {
            case 0: EraseRow(cursor.Row, cursor.Col, Cols); break;
            case 1: EraseRow(cursor.Row, 0, cursor.Col + 1); break;
            case 2: EraseRow(cursor.Row, 0, Cols); break;
        }
    }

    // Erase N chars from cursor position
    public void EraseChars(int count)
    {
        ref var cursor = ref ActiveCursor;
        int end = Math.Min(cursor.Col + count, Cols);
        EraseRow(cursor.Row, cursor.Col, end);
    }

    public void InsertLines(int count)
    {
        ref var cursor = ref ActiveCursor;
        for (int i = 0; i < count; i++)
            InsertLineAt(cursor.Row);
    }

    public void DeleteLines(int count)
    {
        ref var cursor = ref ActiveCursor;
        for (int i = 0; i < count; i++)
            DeleteLineAt(cursor.Row);
    }

    public void InsertChars(int count)
    {
        ref var cursor = ref ActiveCursor;
        var screen = ActiveScreen;
        int row = cursor.Row;
        // Shift cells right
        for (int c = Cols - 1; c >= cursor.Col + count; c--)
            screen[row, c] = screen[row, c - count];
        for (int c = cursor.Col; c < Math.Min(cursor.Col + count, Cols); c++)
            screen[row, c] = TerminalCell.Empty;
        MarkDirty(row);
    }

    public void DeleteChars(int count)
    {
        ref var cursor = ref ActiveCursor;
        var screen = ActiveScreen;
        int row = cursor.Row;
        for (int c = cursor.Col; c < Cols - count; c++)
            screen[row, c] = screen[row, c + count];
        for (int c = Cols - count; c < Cols; c++)
            screen[row, c] = TerminalCell.Empty;
        MarkDirty(row);
    }

    public void ScrollUp(int count)
    {
        var screen = ActiveScreen;
        for (int i = 0; i < count; i++)
        {
            // Push top row into scrollback only when region starts at top of screen
            if (!_usingAlternate && _scrollTop == 0)
                PushScrollback(screen, _scrollTop);

            // Shift rows up within the scroll region
            for (int r = _scrollTop; r < _scrollBottom; r++)
            {
                for (int c = 0; c < Cols; c++)
                    screen[r, c] = screen[r + 1, c];
                MarkDirty(r);
            }
            // Clear bottom row of region
            EraseRow(_scrollBottom, 0, Cols);
        }
    }

    public void ScrollDown(int count)
    {
        var screen = ActiveScreen;
        for (int i = 0; i < count; i++)
        {
            for (int r = _scrollBottom; r > _scrollTop; r--)
            {
                for (int c = 0; c < Cols; c++)
                    screen[r, c] = screen[r - 1, c];
                MarkDirty(r);
            }
            EraseRow(_scrollTop, 0, Cols);
        }
    }

    // Alternate screen (DECALTBUF — \x1b[?1049h / \x1b[?1049l)
    public void EnterAlternateScreen()
    {
        if (_usingAlternate) return;
        _usingAlternate = true;
        FillWithEmpty(_alternate);
        _alternateCursor = new CursorState();
        // VT spec: scroll region resets to full screen on alt-screen enter
        _scrollTop = 0;
        _scrollBottom = Rows - 1;
    }

    public void ExitAlternateScreen()
    {
        if (!_usingAlternate) return;
        _usingAlternate = false;
        // VT spec: scroll region resets to full screen on alt-screen exit
        _scrollTop = 0;
        _scrollBottom = Rows - 1;
    }

    public bool IsAlternateScreen => _usingAlternate;

    // Save/restore cursor (DECSC/DECRC)
    public void SaveCursor() => _savedCursor = ActiveCursor;
    public void RestoreCursor() => ActiveCursor = _savedCursor;

    public void SetCursorVisible(bool visible) => ActiveCursor.Visible = visible;
    public void SetCursorShape(int shape) => _cursorShape = shape;
    public void SetSyncOutput(bool active) => _syncOutputActive = active;
    public void SetBracketedPaste(bool active) => _bracketedPasteActive = active;

    /// <summary>Sets the mouse tracking protocol; <paramref name="mode"/> 0 turns it off.</summary>
    public void SetMouseProtocol(int mode)
    {
        // Whatever lands here is later re-emitted verbatim into the reconnect snapshot's
        // DECSET, so reject anything that isn't a protocol xterm.js models. AnsiParser's
        // switch can never pass a bad value; this guards any future caller.
        if (mode is not (0 or 9 or 1000 or 1002 or 1003))
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Not a mouse tracking protocol mode.");
        _mouseProtocolMode = mode;
    }

    /// <summary>Sets the mouse report encoding; <paramref name="mode"/> 0 restores the default.</summary>
    public void SetMouseEncoding(int mode)
    {
        if (mode is not (0 or 1006))
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Not a mouse report encoding mode.");
        _mouseEncodingMode = mode;
    }

    public void SetFocusReporting(bool active) => _focusReportingActive = active;

    public void ClearInputReportingModes()
    {
        _mouseProtocolMode = 0;
        _mouseEncodingMode = 0;
        _focusReportingActive = false;
    }

    /// <summary>
    /// The modes a reconnecting viewer must be given to reproduce the app's current
    /// mouse/focus reporting state, in the order they should be emitted (protocol,
    /// encoding, focus). Empty when nothing is enabled. Returns a fresh array, so it
    /// is safe to hold onto outside the emulator lock.
    /// </summary>
    public int[] GetInputReportingModes()
    {
        int count = (_mouseProtocolMode != 0 ? 1 : 0)
                  + (_mouseEncodingMode != 0 ? 1 : 0)
                  + (_focusReportingActive ? 1 : 0);
        if (count == 0) return [];

        var modes = new int[count];
        int i = 0;
        if (_mouseProtocolMode != 0) modes[i++] = _mouseProtocolMode;
        if (_mouseEncodingMode != 0) modes[i++] = _mouseEncodingMode;
        if (_focusReportingActive)   modes[i] = 1004;
        return modes;
    }

    // Resize — preserve as much content as possible
    public void Resize(int newCols, int newRows)
    {
        bool wasFullHeight = (_scrollTop == 0 && _scrollBottom == Rows - 1);

        var newNormal    = new TerminalCell[newRows, newCols];
        var newAlternate = new TerminalCell[newRows, newCols];
        FillWithEmpty(newNormal);
        FillWithEmpty(newAlternate);

        int copyRows = Math.Min(Rows, newRows);
        int copyCols = Math.Min(Cols, newCols);

        for (int r = 0; r < copyRows; r++)
            for (int c = 0; c < copyCols; c++)
            {
                newNormal[r, c]    = _normal[r, c];
                newAlternate[r, c] = _alternate[r, c];
            }

        _normal    = newNormal;
        _alternate = newAlternate;
        Cols = newCols;
        Rows = newRows;

        // Clamp cursors
        _normalCursor.Row    = Math.Min(_normalCursor.Row,    newRows - 1);
        _normalCursor.Col    = Math.Min(_normalCursor.Col,    newCols - 1);
        _alternateCursor.Row = Math.Min(_alternateCursor.Row, newRows - 1);
        _alternateCursor.Col = Math.Min(_alternateCursor.Col, newCols - 1);

        // Update scroll region
        if (wasFullHeight)
        {
            _scrollTop    = 0;
            _scrollBottom = newRows - 1;
        }
        else
        {
            _scrollTop    = Math.Min(_scrollTop, newRows - 1);
            _scrollBottom = Math.Clamp(_scrollBottom, _scrollTop, newRows - 1);
        }

        // Resize tab stops — extend with defaults for new columns
        var newTabStops = new bool[newCols];
        int copyTabCols = Math.Min(_tabStops.Length, newCols);
        Array.Copy(_tabStops, newTabStops, copyTabCols);
        for (int c = copyTabCols; c < newCols; c++)
            newTabStops[c] = (c % 8 == 0);
        _tabStops = newTabStops;

        _dirtyRows = new bool[newRows];
        Array.Fill(_dirtyRows, true);
    }

    // ------------------------------------------------------------------
    // Snapshot / dirty tracking
    // ------------------------------------------------------------------

    /// <summary>
    /// Returns scrollback history as an ordered array of rows, oldest first.
    /// Each row is a TerminalCell[] of length Cols at the time it was pushed.
    /// Only populated from the normal screen — alternate screen has no scrollback.
    /// </summary>
    public TerminalCell[][] GetScrollback()
    {
        var result = new TerminalCell[_scrollbackCount][];
        for (int i = 0; i < _scrollbackCount; i++)
        {
            int idx = (_scrollbackHead - _scrollbackCount + i + ScrollbackSize) % ScrollbackSize;
            result[i] = (TerminalCell[])_scrollback[idx].Clone();
        }
        return result;
    }

    /// <summary>Returns a copy of the current screen as a 2D cell array.</summary>
    public TerminalCell[,] GetSnapshot()
    {
        var snap = new TerminalCell[Rows, Cols];
        var screen = ActiveScreen;
        Array.Copy(screen, snap, screen.Length);
        return snap;
    }

    /// <summary>Returns indices of rows modified since last call to ClearDirty.</summary>
    public int[] GetDirtyRows()
    {
        var result = new List<int>(Rows);
        for (int r = 0; r < Rows && r < _dirtyRows.Length; r++)
            if (_dirtyRows[r]) result.Add(r);
        return result.ToArray();
    }

    public void ClearDirty()
    {
        Array.Clear(_dirtyRows, 0, _dirtyRows.Length);
    }

    // ------------------------------------------------------------------
    // Private helpers
    // ------------------------------------------------------------------

    private void InitDefaultTabStops()
    {
        for (int c = 0; c < Cols; c++)
            _tabStops[c] = (c % 8 == 0);
    }

    private void EraseRow(int row, int fromCol, int toColExclusive)
    {
        var screen = ActiveScreen;
        for (int c = fromCol; c < toColExclusive && c < Cols; c++)
            screen[row, c] = TerminalCell.Empty;
        MarkDirty(row);
    }

    private void InsertLineAt(int row)
    {
        var screen = ActiveScreen;
        // Shift rows down within the scroll region
        for (int r = _scrollBottom; r > row; r--)
        {
            for (int c = 0; c < Cols; c++)
                screen[r, c] = screen[r - 1, c];
            MarkDirty(r);
        }
        EraseRow(row, 0, Cols);
    }

    private void DeleteLineAt(int row)
    {
        var screen = ActiveScreen;
        for (int r = row; r < _scrollBottom; r++)
        {
            for (int c = 0; c < Cols; c++)
                screen[r, c] = screen[r + 1, c];
            MarkDirty(r);
        }
        EraseRow(_scrollBottom, 0, Cols);
    }

    private void PushScrollback(TerminalCell[,] screen, int row)
    {
        var line = new TerminalCell[Cols];
        for (int c = 0; c < Cols; c++)
            line[c] = screen[row, c];

        _scrollback[_scrollbackHead] = line;
        _scrollbackHead = (_scrollbackHead + 1) % ScrollbackSize;
        if (_scrollbackCount < ScrollbackSize) _scrollbackCount++;
    }

    private void MarkDirty(int row)
    {
        if (row >= 0 && row < _dirtyRows.Length)
            _dirtyRows[row] = true;
    }

    private static void FillWithEmpty(TerminalCell[,] screen)
    {
        int rows = screen.GetLength(0);
        int cols = screen.GetLength(1);
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
                screen[r, c] = TerminalCell.Empty;
    }

    private struct CursorState
    {
        public int Row;
        public int Col;
        public bool Visible;

        public CursorState() { Row = 0; Col = 0; Visible = true; }
    }
}
