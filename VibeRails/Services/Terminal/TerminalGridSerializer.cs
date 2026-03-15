using System.Text;
using TerminalEmulator;

namespace VibeRails.Services.Terminal;

/// <summary>
/// Converts a TerminalEmulator grid snapshot into an ANSI byte stream that
/// xterm.js can replay to reconstruct the exact current screen state.
/// </summary>
internal static class TerminalGridSerializer
{
    /// <summary>
    /// Serializes the grid to a UTF-8 encoded ANSI byte stream.
    /// Preamble: clear screen + home cursor.
    /// Each row is rendered with delta SGR encoding (only emits codes on change).
    /// Cursor is repositioned to (cursorRow, cursorCol) at the end.
    /// </summary>
    public static byte[] Serialize(
        TerminalCell[,] snap,
        int rows, int cols,
        int cursorRow, int cursorCol)
    {
        // Rough capacity: clear+home (7) + per-row cursor move (10) + cols * ~8 bytes avg
        var sb = new StringBuilder(rows * cols * 4 + rows * 12 + 64);

        // Clear screen and home cursor
        sb.Append("\x1b[2J\x1b[H");

        for (int r = 0; r < rows; r++)
        {
            // Move cursor to start of this row (1-based)
            sb.Append("\x1b[");
            sb.Append(r + 1);
            sb.Append(";1H");

            CellColor lastFg = default;
            CellColor lastBg = default;
            CellAttributes lastAttrs = CellAttributes.None;
            bool firstCell = true;

            for (int c = 0; c < cols; c++)
            {
                var cell = snap[r, c];
                if (cell.IsWideContinuation) continue;

                // Emit SGR only when something changed
                if (firstCell || cell.Fg != lastFg || cell.Bg != lastBg || cell.Attributes != lastAttrs)
                {
                    var sgr = BuildSgr(cell.Fg, cell.Bg, cell.Attributes,
                                       lastFg, lastBg, lastAttrs, firstCell);
                    if (sgr.Length > 0)
                        sb.Append(sgr);
                    lastFg = cell.Fg;
                    lastBg = cell.Bg;
                    lastAttrs = cell.Attributes;
                    firstCell = false;
                }

                char ch = cell.Char;
                if (ch == '\0' || ch < ' ') ch = ' ';
                sb.Append(ch);
            }

            // Reset colors at end of each row
            sb.Append("\x1b[0m");
        }

        // Reposition cursor to where the terminal's cursor actually is (1-based)
        sb.Append("\x1b[");
        sb.Append(Math.Clamp(cursorRow + 1, 1, rows));
        sb.Append(';');
        sb.Append(Math.Clamp(cursorCol + 1, 1, cols));
        sb.Append('H');

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static string BuildSgr(
        CellColor fg, CellColor bg, CellAttributes attrs,
        CellColor prevFg, CellColor prevBg, CellAttributes prevAttrs,
        bool reset)
    {
        var parts = new List<string>(8);

        if (reset) parts.Add("0");

        // Attributes
        if (reset || attrs != prevAttrs)
        {
            if (attrs.HasFlag(CellAttributes.Bold))      parts.Add("1");
            if (attrs.HasFlag(CellAttributes.Dim))       parts.Add("2");
            if (attrs.HasFlag(CellAttributes.Italic))    parts.Add("3");
            if (attrs.HasFlag(CellAttributes.Underline)) parts.Add("4");
            if (attrs.HasFlag(CellAttributes.Inverse))   parts.Add("7");
        }

        // Foreground
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

        // Background
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
