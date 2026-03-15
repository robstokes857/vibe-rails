using System.Text;
using TerminalEmulator;

namespace VibeRails.Services.Terminal;

/// <summary>
/// Converts a TerminalEmulator snapshot (scrollback + current screen) into an
/// ANSI byte stream that xterm.js renders instantly on reconnect — no animation,
/// no DB, just the exact current state including full scroll history.
/// </summary>
internal static class TerminalGridSerializer
{
    /// <summary>
    /// Serializes scrollback rows followed by the current screen grid.
    /// The result is a single ANSI byte stream:
    ///   - Hard reset to clear any stale client state
    ///   - Scrollback rows (oldest first) with SGR colors preserved
    ///   - Current screen rows
    ///   - Cursor repositioned to match terminal cursor
    /// </summary>
    public static byte[] Serialize(
        TerminalCell[][] scrollback,
        TerminalCell[,] screen,
        int rows, int cols,
        int cursorRow, int cursorCol)
    {
        int scrollbackCount = scrollback.Length;
        // Rough capacity: reset(4) + per-row(cols*8 avg + newline) for scrollback + screen
        var sb = new StringBuilder((scrollbackCount + rows) * cols * 4 + (scrollbackCount + rows) * 16 + 64);

        // Hard reset — clears xterm.js completely including its own scrollback
        sb.Append("\x1bc");

        // Scrollback rows (plain ANSI, newline-terminated)
        foreach (var row in scrollback)
        {
            SerializeRow(sb, row, Math.Min(row.Length, cols));
            sb.Append("\r\n");
        }

        // Current screen rows
        for (int r = 0; r < rows; r++)
        {
            SerializeScreenRow(sb, screen, r, cols);
            if (r < rows - 1)
                sb.Append("\r\n");
        }

        // Reposition cursor to where the terminal's cursor actually is
        sb.Append("\x1b[");
        sb.Append(Math.Clamp(cursorRow + 1, 1, rows));
        sb.Append(';');
        sb.Append(Math.Clamp(cursorCol + 1, 1, cols));
        sb.Append('H');

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static void SerializeRow(StringBuilder sb, TerminalCell[] row, int cols)
    {
        CellColor lastFg = default;
        CellColor lastBg = default;
        CellAttributes lastAttrs = CellAttributes.None;
        bool firstCell = true;

        for (int c = 0; c < cols; c++)
        {
            var cell = row[c];
            if (cell.IsWideContinuation) continue;

            if (firstCell || cell.Fg != lastFg || cell.Bg != lastBg || cell.Attributes != lastAttrs)
            {
                var sgr = BuildSgr(cell.Fg, cell.Bg, cell.Attributes, lastFg, lastBg, lastAttrs, firstCell);
                if (sgr.Length > 0) sb.Append(sgr);
                lastFg = cell.Fg;
                lastBg = cell.Bg;
                lastAttrs = cell.Attributes;
                firstCell = false;
            }

            char ch = cell.Char;
            if (ch == '\0' || ch < ' ') ch = ' ';
            sb.Append(ch);
        }

        sb.Append("\x1b[0m");
    }

    private static void SerializeScreenRow(StringBuilder sb, TerminalCell[,] screen, int row, int cols)
    {
        CellColor lastFg = default;
        CellColor lastBg = default;
        CellAttributes lastAttrs = CellAttributes.None;
        bool firstCell = true;

        for (int c = 0; c < cols; c++)
        {
            var cell = screen[row, c];
            if (cell.IsWideContinuation) continue;

            if (firstCell || cell.Fg != lastFg || cell.Bg != lastBg || cell.Attributes != lastAttrs)
            {
                var sgr = BuildSgr(cell.Fg, cell.Bg, cell.Attributes, lastFg, lastBg, lastAttrs, firstCell);
                if (sgr.Length > 0) sb.Append(sgr);
                lastFg = cell.Fg;
                lastBg = cell.Bg;
                lastAttrs = cell.Attributes;
                firstCell = false;
            }

            char ch = cell.Char;
            if (ch == '\0' || ch < ' ') ch = ' ';
            sb.Append(ch);
        }

        sb.Append("\x1b[0m");
    }

    private static string BuildSgr(
        CellColor fg, CellColor bg, CellAttributes attrs,
        CellColor prevFg, CellColor prevBg, CellAttributes prevAttrs,
        bool reset)
    {
        var parts = new List<string>(8);

        if (reset) parts.Add("0");

        if (reset || attrs != prevAttrs)
        {
            if (attrs.HasFlag(CellAttributes.Bold))      parts.Add("1");
            if (attrs.HasFlag(CellAttributes.Dim))       parts.Add("2");
            if (attrs.HasFlag(CellAttributes.Italic))    parts.Add("3");
            if (attrs.HasFlag(CellAttributes.Underline)) parts.Add("4");
            if (attrs.HasFlag(CellAttributes.Inverse))   parts.Add("7");
        }

        if (reset || fg != prevFg)
        {
            if (fg.IsDefault)
                parts.Add("39");
            else if (fg.IsPalette && fg.PaletteIndex < 8)
                parts.Add((30 + fg.PaletteIndex).ToString());
            else if (fg.IsPalette && fg.PaletteIndex < 16)
                parts.Add((90 + fg.PaletteIndex - 8).ToString());
            else if (fg.IsPalette)
                { parts.Add("38"); parts.Add("5"); parts.Add(fg.PaletteIndex.ToString()); }
            else if (fg.IsRgb)
                { parts.Add("38"); parts.Add("2"); parts.Add(fg.R.ToString()); parts.Add(fg.G.ToString()); parts.Add(fg.B.ToString()); }
        }

        if (reset || bg != prevBg)
        {
            if (bg.IsDefault)
                parts.Add("49");
            else if (bg.IsPalette && bg.PaletteIndex < 8)
                parts.Add((40 + bg.PaletteIndex).ToString());
            else if (bg.IsPalette && bg.PaletteIndex < 16)
                parts.Add((100 + bg.PaletteIndex - 8).ToString());
            else if (bg.IsPalette)
                { parts.Add("48"); parts.Add("5"); parts.Add(bg.PaletteIndex.ToString()); }
            else if (bg.IsRgb)
                { parts.Add("48"); parts.Add("2"); parts.Add(bg.R.ToString()); parts.Add(bg.G.ToString()); parts.Add(bg.B.ToString()); }
        }

        if (parts.Count == 0) return string.Empty;

        var sb = new StringBuilder(32);
        sb.Append("\x1b[");
        sb.Append(string.Join(";", parts));
        sb.Append('m');
        return sb.ToString();
    }
}
