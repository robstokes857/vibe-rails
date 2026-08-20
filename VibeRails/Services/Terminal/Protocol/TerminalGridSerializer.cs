using System.Buffers;
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
        bool isAlternateScreen = false,
        bool bracketedPaste = false,
        IReadOnlyList<int>? inputReportingModes = null)
    {
        int scrollbackCount = scrollback.Length;
        // Rough capacity: reset prologue + per-row(cols*8 avg + newline) for scrollback + screen
        var sb = new StringBuilder((scrollbackCount + rows) * cols * 4 + (scrollbackCount + rows) * 16 + 64);

        AppendSnapshotResetPrologue(sb);

        // The prologue resets bracketed-paste (?2004) off. CLIs emit ?2004h once at
        // startup and never re-send it, so a reconnecting viewer that only gets the
        // snapshot would lose the mode — breaking the webview's Shift+Enter→newline
        // and multi-line paste, which gate on xterm tracking ?2004. Restore it here
        // (screen-independent, like alt-screen below) to match the live app's state.
        if (bracketedPaste)
        {
            sb.Append("\x1b[?2004h");
        }

        // Same story for mouse tracking / focus reporting (?1000/1002/1003/1004/
        // 1005/1006/1015): the prologue resets them, CLIs set them once at startup
        // and never re-assert (opentui does not re-assert even on SIGWINCH), so
        // without this a reconnected viewer loses mouse reporting for the rest of
        // the session. xterm.js then falls back to alt-scroll — emitting cursor-up/
        // down for wheel events, which OpenCode's composer reads as input-history
        // navigation. Re-emit exactly the modes the app enabled, in one DECSET.
        // See runbooks/terminal/TERMINAL.md "## 2026-07-26 GLM 5.2 wheel".
        if (inputReportingModes is { Count: > 0 })
        {
            sb.Append("\x1b[?");
            for (int i = 0; i < inputReportingModes.Count; i++)
            {
                if (i > 0) sb.Append(';');
                sb.Append(inputReportingModes[i]);
            }
            sb.Append('h');
        }

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

        return EncodeUtf8(sb);
    }

    /// <summary>
    /// UTF-8 encodes the builder without materializing a flat string first. For a full
    /// 20k-line scrollback snapshot, sb.ToString() alone is tens of MB on the large
    /// object heap — allocated while the PTY read loop is blocked behind the
    /// subscriber lock. The builder is copied into ONE contiguous pooled buffer and
    /// encoded in a single stateless pass. Do NOT rewrite this as per-chunk Encoder
    /// calls: StringBuilder chunks can split a surrogate pair (emoji in TUI output),
    /// and Encoder.GetByteCount does not carry pair state across calls, so a chunked
    /// count pass disagrees with the GetBytes pass by a byte and encoding throws
    /// mid-snapshot. Contiguous input has no chunk boundaries and no encoder state.
    /// </summary>
    internal static byte[] EncodeUtf8(StringBuilder sb)
    {
        int length = sb.Length;
        if (length == 0)
        {
            return Array.Empty<byte>();
        }

        char[] rented = ArrayPool<char>.Shared.Rent(length);
        try
        {
            sb.CopyTo(0, rented.AsSpan(0, length), length);
            var chars = rented.AsSpan(0, length);
            var bytes = new byte[Encoding.UTF8.GetByteCount(chars)];
            Encoding.UTF8.GetBytes(chars, bytes);
            return bytes;
        }
        finally
        {
            ArrayPool<char>.Shared.Return(rented);
        }
    }

    private static void AppendSnapshotResetPrologue(StringBuilder sb)
    {
        // Snapshot replay must be self-contained. A viewer may have disconnected
        // while xterm.js was in alt-screen, bracketed-paste, mouse tracking,
        // synchronized-output, application-cursor, or a custom scroll region.
        // Do not use RIS here; this explicit baseline avoids historical xterm.js
        // cursor artifacts while still returning replay to factory-like modes.
        sb.Append("\x1b[?1049;1047;47l"); // main screen
        // Every mode tracked for the restore block above must be reset here too, or a
        // viewer holding it from live traffic keeps it across reconnect. ?9 (X10 mouse)
        // is in the tracked protocol set, so it is in this list.
        sb.Append("\x1b[?1;6;9;1000;1002;1003;1004;1005;1006;1007;1015;2004;2026l");
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
