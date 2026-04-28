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
        int cursorRow, int cursorCol,
        bool cursorVisible = true,
        int cursorShape = 0,
        bool isAlternateScreen = false)
    {
        int scrollbackCount = scrollback.Length;
        // Rough capacity: reset prologue + per-row(cols*8 avg + newline) for scrollback + screen
        var sb = new StringBuilder((scrollbackCount + rows) * cols * 4 + (scrollbackCount + rows) * 16 + 64);

        AppendSnapshotResetPrologue(sb);

        if (isAlternateScreen)
        {
            // Terminal is in alt-screen — put xterm.js into alt-screen mode before
            // rendering content. Skip scrollback (alt-screen has none per VT spec).
            sb.Append("\x1b[?1049h");
            sb.Append("\x1b[?25l");
            sb.Append("\x1b[2J");
            sb.Append("\x1b[H");
        }
        else
        {
            // Normal screen — replay scrollback rows (plain ANSI, newline-terminated)
            foreach (var row in scrollback)
            {
                SerializeRow(sb, row, Math.Min(row.Length, cols));
                sb.Append("\r\n");
            }
        }

        // Current screen rows — use absolute CUP addressing per row to prevent
        // cumulative cursor drift (wrap semantics, wide chars, full-width columns).
        for (int r = 0; r < rows; r++)
        {
            sb.Append($"\x1b[{r + 1};1H");
            SerializeScreenRow(sb, screen, r, cols);
        }

        // Reset attributes, then reposition cursor to match the terminal's real cursor.
        // If the cursor is parked outside the visible area (e.g. Copilot parks its idle
        // cursor at col 295 in a 185-col terminal), treat it as hidden rather than
        // clamping to the screen edge — clamping produces a visible phantom cursor at
        // the far right of the last row which confuses users on reconnect.
        bool cursorInBounds = cursorRow >= 0 && cursorRow < rows && cursorCol >= 0 && cursorCol < cols;
        bool effectivelyVisible = cursorVisible && cursorInBounds;

        sb.Append("\x1b[0m");
        if (cursorInBounds)
        {
            sb.Append("\x1b[");
            sb.Append(cursorRow + 1);
            sb.Append(';');
            sb.Append(cursorCol + 1);
            sb.Append('H');
        }

        // Restore cursor visibility and shape to match the emulator's actual state.
        // TUIs that hide the cursor (drawing their own block glyph) will have
        // cursorVisible=false, so the cursor stays hidden — no ghost cursor.
        // Regular shells with cursorVisible=true get it shown at the right position.
        sb.Append(effectivelyVisible ? "\x1b[?25h" : "\x1b[?25l");

        // Restore cursor shape (DECSCUSR: CSI Ps SP q). Always emit so xterm.js
        // matches the app's cursor style rather than its own default.
        // 0=default, 1=blinking block, 2=steady block, 3=blinking underline,
        // 4=steady underline, 5=blinking bar, 6=steady bar
        sb.Append("\x1b[");
        sb.Append(cursorShape);
        sb.Append(" q");

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static void AppendSnapshotResetPrologue(StringBuilder sb)
    {
        // Snapshot replay must be self-contained. A viewer may have disconnected
        // while xterm.js was in alt-screen, bracketed-paste, mouse tracking,
        // synchronized-output, application-cursor, or a custom scroll region.
        // Do not use RIS here; this explicit baseline avoids historical xterm.js
        // cursor artifacts while still returning replay to factory-like modes.
        sb.Append("\x1b[?1049;1047;47l"); // main screen
        sb.Append("\x1b[?1;6;1000;1002;1003;1004;1005;1006;1007;1015;2004;2026l");
        sb.Append("\x1b[?7h");           // autowrap on
        sb.Append("\x1b>");              // normal keypad
        sb.Append("\x1b(B\x1b)B");       // default G0/G1 charsets
        sb.Append("\x1b[r");             // full-screen scroll region
        sb.Append("\x1b[0m");            // default attributes
        sb.Append("\x1b[?25l");          // hide cursor during repaint
        sb.Append("\x1b[2J");            // clear visible screen
        sb.Append("\x1b[3J");            // clear scrollback
        sb.Append("\x1b[H");             // home cursor (1,1)
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

            cell.AppendText(sb, replaceControlWithSpace: true);
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

            cell.AppendText(sb, replaceControlWithSpace: true);
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
