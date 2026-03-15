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

    public int Cols { get; private set; }
    public int Rows { get; private set; }
    public int ScrollbackSize { get; }

    public int CursorCol => ActiveCursor.Col;
    public int CursorRow => ActiveCursor.Row;
    public bool CursorVisible => ActiveCursor.Visible;

    // Tracks which rows changed since last snapshot — cleared by consumer
    private readonly bool[] _dirtyRows;

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

        FillWithEmpty(_normal);
        FillWithEmpty(_alternate);
    }

    // ------------------------------------------------------------------
    // Screen / cursor operations called by AnsiParser
    // ------------------------------------------------------------------

    public void WriteChar(char ch, bool wideChar = false)
    {
        ref var cursor = ref ActiveCursor;
        if (cursor.Col >= Cols)
        {
            cursor.Col = 0;
            LineFeed(scroll: true);
        }

        var screen = ActiveScreen;
        screen[cursor.Row, cursor.Col] = new TerminalCell
        {
            Char = ch,
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
        if (cursor.Row < Rows - 1)
        {
            cursor.Row++;
        }
        else if (scroll)
        {
            ScrollUp(1);
        }
    }

    public void ReverseLineFeed()
    {
        ref var cursor = ref ActiveCursor;
        if (cursor.Row > 0)
            cursor.Row--;
        else
            InsertLineAt(0);
    }

    public void Tab()
    {
        ref var cursor = ref ActiveCursor;
        int next = ((cursor.Col / 8) + 1) * 8;
        cursor.Col = Math.Min(next, Cols - 1);
    }

    public void Backspace()
    {
        ref var cursor = ref ActiveCursor;
        if (cursor.Col > 0) cursor.Col--;
    }

    // Erase in display
    public void EraseInDisplay(int mode)
    {
        ref var cursor = ref ActiveCursor;
        var screen = ActiveScreen;
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
            case 3: // whole screen + scrollback (treat same as 2 for now)
                for (int r = 0; r < Rows; r++) EraseRow(r, 0, Cols);
                break;
        }
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
            // Push top row into scrollback
            if (!_usingAlternate)
                PushScrollback(screen, 0);

            // Shift rows up
            for (int r = 0; r < Rows - 1; r++)
            {
                for (int c = 0; c < Cols; c++)
                    screen[r, c] = screen[r + 1, c];
                MarkDirty(r);
            }
            // Clear bottom row
            EraseRow(Rows - 1, 0, Cols);
        }
    }

    public void ScrollDown(int count)
    {
        var screen = ActiveScreen;
        for (int i = 0; i < count; i++)
        {
            for (int r = Rows - 1; r > 0; r--)
            {
                for (int c = 0; c < Cols; c++)
                    screen[r, c] = screen[r - 1, c];
                MarkDirty(r);
            }
            EraseRow(0, 0, Cols);
        }
    }

    // Alternate screen (DECALTBUF — \x1b[?1049h / \x1b[?1049l)
    public void EnterAlternateScreen()
    {
        if (_usingAlternate) return;
        _usingAlternate = true;
        FillWithEmpty(_alternate);
        _alternateCursor = new CursorState();
    }

    public void ExitAlternateScreen()
    {
        if (!_usingAlternate) return;
        _usingAlternate = false;
    }

    public bool IsAlternateScreen => _usingAlternate;

    // Save/restore cursor (DECSC/DECRC)
    public void SaveCursor() => _savedCursor = ActiveCursor;
    public void RestoreCursor() => ActiveCursor = _savedCursor;

    public void SetCursorVisible(bool visible) => ActiveCursor.Visible = visible;

    // Resize — preserve as much content as possible
    public void Resize(int newCols, int newRows)
    {
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
    }

    // ------------------------------------------------------------------
    // Snapshot / dirty tracking
    // ------------------------------------------------------------------

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
        for (int r = Rows - 1; r > row; r--)
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
        for (int r = row; r < Rows - 1; r++)
        {
            for (int c = 0; c < Cols; c++)
                screen[r, c] = screen[r + 1, c];
            MarkDirty(r);
        }
        EraseRow(Rows - 1, 0, Cols);
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
