namespace TerminalEmulator;

/// <summary>
/// The public API for the terminal state proxy.
///
/// Usage:
///   var terminal = new Terminal(cols: 220, rows: 50);
///   terminal.Write(rawPtyBytes);          // feed PTY output
///   terminal.OnRender += dirtyRows => {}; // subscribe to changes
///   var snap = terminal.GetSnapshot();    // full grid for new clients
///   terminal.Resize(newCols, newRows);    // propagate PTY resize
///
/// Thread safety: Write/Resize are NOT thread-safe. If you read from the
/// PTY on a background thread, use a lock or channel to serialize calls.
/// GetSnapshot() returns a copy, so it is safe to read after Write completes.
/// </summary>
public sealed class Terminal
{
    private readonly TerminalBuffer _buffer;
    private readonly AnsiParser _parser;

    public int Cols => _buffer.Cols;
    public int Rows => _buffer.Rows;
    public int CursorCol => _buffer.CursorCol;
    public int CursorRow => _buffer.CursorRow;
    public bool CursorVisible => _buffer.CursorVisible;
    public bool IsAlternateScreen => _buffer.IsAlternateScreen;
    public CellAttributes CurrentAttributes => _buffer.CurrentAttributes;
    public CellColor CurrentFg => _buffer.CurrentFg;
    public CellColor CurrentBg => _buffer.CurrentBg;

    /// <summary>
    /// Fired after each Write() call with the row indices that changed.
    /// Empty array means Write() was a no-op (e.g. all ignored sequences).
    /// Subscribers must not call Write() or Resize() inside this event.
    /// </summary>
    public event Action<int[]>? OnRender;

    public Terminal(int cols = 80, int rows = 24, int scrollbackSize = 1000)
    {
        _buffer = new TerminalBuffer(cols, rows, scrollbackSize);
        _parser = new AnsiParser(_buffer);
    }

    /// <summary>Write raw PTY bytes (may include ANSI escape sequences).</summary>
    public void Write(ReadOnlySpan<byte> data)
    {
        _parser.Parse(data);
        var dirty = _buffer.GetDirtyRows();
        _buffer.ClearDirty();
        if (dirty.Length > 0)
            OnRender?.Invoke(dirty);
    }

    /// <summary>Write raw PTY bytes from a string (UTF-8 encoded). Convenience overload.</summary>
    public void Write(string data)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(data);
        Write(bytes.AsSpan());
    }

    /// <summary>Resize the terminal grid. Call this when the PTY is resized.</summary>
    public void Resize(int cols, int rows)
    {
        _buffer.Resize(cols, rows);
        var dirty = _buffer.GetDirtyRows();
        _buffer.ClearDirty();
        if (dirty.Length > 0)
            OnRender?.Invoke(dirty);
    }

    /// <summary>
    /// Returns a snapshot of the current screen. Safe to hold onto — it's a copy.
    /// Row 0 is the top of the screen.
    /// </summary>
    public TerminalCell[,] GetSnapshot() => _buffer.GetSnapshot();

    /// <summary>
    /// Returns the text content of a single row as a string (trailing spaces trimmed).
    /// </summary>
    public string GetRowText(int row)
    {
        var snap = _buffer.GetSnapshot();
        var sb = new System.Text.StringBuilder(Cols);
        for (int c = 0; c < Cols; c++)
        {
            char ch = snap[row, c].Char;
            sb.Append(ch == '\0' ? ' ' : ch);
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Returns the full screen as plain text lines (no color/attribute info).
    /// Useful for quick assertions in tests.
    /// </summary>
    public string[] GetScreenText()
    {
        var lines = new string[Rows];
        for (int r = 0; r < Rows; r++)
            lines[r] = GetRowText(r);
        return lines;
    }

    /// <summary>
    /// Returns scrollback history as an ordered array of rows, oldest first.
    /// Each row is a TerminalCell[] of length Cols at time of scroll.
    /// </summary>
    public TerminalCell[][] GetScrollback() => _buffer.GetScrollback();

    /// <summary>Reset the terminal to initial state.</summary>
    public void Reset()
    {
        _buffer.ExitAlternateScreen();
        _buffer.EraseInDisplay(2);
        _buffer.MoveCursorTo(0, 0);
        _buffer.CurrentFg = CellColor.Default;
        _buffer.CurrentBg = CellColor.Default;
        _buffer.CurrentAttributes = CellAttributes.None;
        _buffer.ClearDirty();
    }
}
