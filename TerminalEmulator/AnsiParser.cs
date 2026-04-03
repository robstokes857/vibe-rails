namespace TerminalEmulator;

/// <summary>
/// VT/ANSI escape sequence parser. Pure state machine — no reflection, no allocation
/// on hot path. Feed raw bytes from the PTY one span at a time via Parse().
///
/// Implements a subset of the DEC/xterm sequence set covering everything produced
/// by PowerShell, bash, vim, htop, and Claude Code TUI (as observed in real captures).
/// </summary>
public sealed class AnsiParser
{
    private readonly TerminalBuffer _buffer;

    // --- Parser state ---
    private ParserState _state = ParserState.Ground;

    // Intermediate chars (e.g. '?' in CSI ? ... )
    private byte _intermediate;

    // CSI/DCS parameters — up to 16 numeric params
    private readonly int[] _params = new int[16];
    private int _paramCount;
    private bool _paramHadDigit; // tracks if current param had any digit

    // OSC string accumulator
    private readonly char[] _oscBuf = new char[2048];
    private int _oscLen;

    // UTF-8 decode state
    private int _utf8Remaining;
    private uint _utf8Codepoint;

    // Last printed graphic character (for REP / CSI b)
    private int _lastPrintedCodepoint;
    private bool _lastPrintedWide = false;

    public AnsiParser(TerminalBuffer buffer)
    {
        _buffer = buffer;
    }

    // ------------------------------------------------------------------
    // Public entry point
    // ------------------------------------------------------------------

    /// <summary>Parse a chunk of raw PTY bytes. Can be called repeatedly as bytes arrive.</summary>
    public void Parse(ReadOnlySpan<byte> data)
    {
        foreach (byte b in data)
            ProcessByte(b);
    }

    // ------------------------------------------------------------------
    // State machine
    // ------------------------------------------------------------------

    private void ProcessByte(byte b)
    {
        // C0 controls are handled in most states
        if (b < 0x20 && _state != ParserState.OscString && _state != ParserState.DcsPassthrough)
        {
            switch (b)
            {
                case 0x00: return; // NUL
                case 0x07: // BEL
                    if (_state == ParserState.OscString) { DispatchOsc(); _state = ParserState.Ground; }
                    return;
                case 0x08: _buffer.Backspace(); return;
                case 0x09: _buffer.Tab(); return;
                case 0x0A: // LF
                case 0x0B: // VT
                case 0x0C: // FF
                    _buffer.LineFeed(scroll: true); return;
                case 0x0D: _buffer.CarriageReturn(); return;
                case 0x0E: return; // SO - ignore charset switch
                case 0x0F: return; // SI - ignore charset switch
                case 0x1B: // ESC
                    _state = ParserState.Escape;
                    _intermediate = 0;
                    return;
                case 0x9B: // CSI (8-bit shortcut)
                    StartCsi();
                    return;
                case 0x9D: // OSC (8-bit shortcut)
                    _state = ParserState.OscString;
                    _oscLen = 0;
                    return;
                default: return;
            }
        }

        switch (_state)
        {
            case ParserState.Ground:
                ProcessUtf8(b);
                break;

            case ParserState.Escape:
                ProcessEscape(b);
                break;

            case ParserState.EscapeIntermediate:
                // e.g. ESC ( B  — charset designations, ignore
                if (b >= 0x30 && b <= 0x7E) _state = ParserState.Ground;
                break;

            case ParserState.CsiEntry:
            case ParserState.CsiParam:
            case ParserState.CsiIntermediate:
                ProcessCsi(b);
                break;

            case ParserState.OscString:
                ProcessOsc(b);
                break;

            case ParserState.DcsPassthrough:
                // DCS data — consume until BEL, 8-bit ST, or ESC (start of ESC \)
                if (b == 0x07 || b == 0x9C)
                    _state = ParserState.Ground;
                else if (b == 0x1B)
                {
                    _state = ParserState.Escape;
                    _intermediate = 0;
                }
                break;
        }
    }

    private void ProcessEscape(byte b)
    {
        // Intermediates (space through /)
        if (b >= 0x20 && b <= 0x2F)
        {
            _intermediate = b;
            _state = ParserState.EscapeIntermediate;
            return;
        }

        switch (b)
        {
            case 0x5B: // [ -> CSI
                StartCsi();
                break;
            case 0x5D: // ] -> OSC
                _state = ParserState.OscString;
                _oscLen = 0;
                break;
            case 0x50: // P -> DCS
                _state = ParserState.DcsPassthrough;
                break;
            case 0x58: // X -> SOS
            case 0x5E: // ^ -> PM
            case 0x5F: // _ -> APC
                _state = ParserState.DcsPassthrough;
                break;
            case 0x44: // D -> IND (Index) — like LF, scroll if at bottom margin
                _buffer.LineFeed(scroll: true);
                _state = ParserState.Ground;
                break;
            case 0x45: // E -> NEL (Next Line)
                _buffer.CarriageReturn();
                _buffer.LineFeed(scroll: true);
                _state = ParserState.Ground;
                break;
            case 0x48: // H -> HTS (Horizontal Tab Set)
                _buffer.SetTabStop();
                _state = ParserState.Ground;
                break;
            case 0x4D: // M -> Reverse Index (RI)
                _buffer.ReverseLineFeed();
                _state = ParserState.Ground;
                break;
            case 0x37: // 7 -> DECSC save cursor
                _buffer.SaveCursor();
                _state = ParserState.Ground;
                break;
            case 0x38: // 8 -> DECRC restore cursor
                _buffer.RestoreCursor();
                _state = ParserState.Ground;
                break;
            case 0x63: // c -> RIS full reset
                FullReset();
                _state = ParserState.Ground;
                break;
            case 0x5C: // \ -> ST (string terminator)
                // OSC/DCS already dispatched when ESC was seen (in ProcessOsc/DcsPassthrough),
                // so this just completes the ESC \ sequence by returning to ground.
                _state = ParserState.Ground;
                break;
            default:
                // Unknown ESC sequence — ignore and return to ground
                _state = ParserState.Ground;
                break;
        }
    }

    private void StartCsi()
    {
        _state = ParserState.CsiEntry;
        _paramCount = 0;
        _params[0] = 0;
        _paramHadDigit = false;
        _intermediate = 0;
    }

    private void ProcessCsi(byte b)
    {
        if (b >= 0x30 && b <= 0x39) // digit
        {
            if (_paramCount < _params.Length)
            {
                _params[_paramCount] = _params[_paramCount] * 10 + (b - 0x30);
                _paramHadDigit = true;
            }
            _state = ParserState.CsiParam;
            return;
        }

        if (b == 0x3B) // ;
        {
            if (_paramCount + 1 < _params.Length)
            {
                _paramCount++;
                _params[_paramCount] = 0;
            }
            _paramHadDigit = false;
            _state = ParserState.CsiParam;
            return;
        }

        if (b == 0x3A) // : (sub-param separator, used in extended color)
        {
            // Treat same as ; for our purposes
            if (_paramCount + 1 < _params.Length)
            {
                _paramCount++;
                _params[_paramCount] = 0;
            }
            _paramHadDigit = false;
            _state = ParserState.CsiParam;
            return;
        }

        // Intermediate bytes (space - /)
        if (b >= 0x20 && b <= 0x2F)
        {
            _intermediate = b;
            _state = ParserState.CsiIntermediate;
            return;
        }

        // Private/final parameter prefix (< = > ? !)
        if (b >= 0x3C && b <= 0x3F && _state == ParserState.CsiEntry)
        {
            _intermediate = b;
            _state = ParserState.CsiParam;
            return;
        }

        // Final byte 0x40-0x7E
        if (b >= 0x40 && b <= 0x7E)
        {
            // Finalise current param count (clamp to array size)
            if ((_paramHadDigit || _paramCount > 0) && _paramCount < _params.Length)
                _paramCount++; // total filled params

            DispatchCsi(b, _intermediate);
            _state = ParserState.Ground;
            return;
        }

        // Anything else — abort
        _state = ParserState.Ground;
    }

    private void ProcessOsc(byte b)
    {
        if (b == 0x07 || b == 0x9C) // BEL or ST
        {
            DispatchOsc();
            _state = ParserState.Ground;
            return;
        }
        if (b == 0x1B) // might be ESC \ (ST)
        {
            // We'll get the \ next; set a flag via DcsPassthrough abuse
            DispatchOsc();
            _state = ParserState.Escape; // ESC seen, next byte should be '\'
            return;
        }
        if (_oscLen < _oscBuf.Length)
            _oscBuf[_oscLen++] = (char)b;
    }

    // ------------------------------------------------------------------
    // Dispatch
    // ------------------------------------------------------------------

    private void DispatchCsi(byte finalByte, byte intermediate)
    {
        int p0 = _paramCount > 0 ? _params[0] : 0;
        int p1 = _paramCount > 1 ? _params[1] : 0;

        // DECSTR soft reset — intermediate '!' final 'p'
        if (intermediate == (byte)'!')
        {
            if ((char)finalByte == 'p') SoftReset();
            return;
        }

        // Private modes — intermediate is '?' or '>'
        if (intermediate == (byte)'?')
        {
            for (int i = 0; i < _paramCount; i++)
                DispatchCsiPrivate(finalByte, _params[i]);
            return;
        }

        if (intermediate == (byte)'>')
        {
            // DA2 etc — ignore
            return;
        }

        switch ((char)finalByte)
        {
            case 'A': _buffer.MoveCursorRelative(-(Math.Max(1, p0)), 0); break;   // CUU
            case 'B': _buffer.MoveCursorRelative(Math.Max(1, p0), 0); break;    // CUD
            case 'C': _buffer.MoveCursorRelative(0, Math.Max(1, p0)); break;    // CUF
            case 'D': _buffer.MoveCursorRelative(0, -(Math.Max(1, p0))); break; // CUB
            case 'E': _buffer.MoveCursorTo(_buffer.CursorRow + Math.Max(1, p0), 0); break; // CNL
            case 'F': _buffer.MoveCursorTo(_buffer.CursorRow - Math.Max(1, p0), 0); break; // CPL
            case 'G': _buffer.SetCursorCol(Math.Max(1, p0) - 1); break;          // CHA
            case 'H': // CUP  ESC[row;colH  (1-based)
            case 'f':
                _buffer.MoveCursorTo(Math.Max(1, p0) - 1, Math.Max(1, p1) - 1);
                break;
            case 'I': // CHT - cursor forward tab
                for (int i = 0; i < Math.Max(1, p0); i++) _buffer.Tab();
                break;
            case 'J': _buffer.EraseInDisplay(p0); break;                         // ED
            case 'K': _buffer.EraseInLine(p0); break;                            // EL
            case 'L': _buffer.InsertLines(Math.Max(1, p0)); break;               // IL
            case 'M': _buffer.DeleteLines(Math.Max(1, p0)); break;               // DL
            case 'P': _buffer.DeleteChars(Math.Max(1, p0)); break;               // DCH
            case 'S': _buffer.ScrollUp(Math.Max(1, p0)); break;                  // SU
            case 'T': _buffer.ScrollDown(Math.Max(1, p0)); break;                // SD
            case 'X': _buffer.EraseChars(Math.Max(1, p0)); break;                // ECH
            case 'Z': // CBT - cursor backward tab (jump to previous tab stop)
                for (int i = 0; i < Math.Max(1, p0); i++) _buffer.BackTab();
                break;
            case '@': _buffer.InsertChars(Math.Max(1, p0)); break;               // ICH
            case 'a': _buffer.MoveCursorRelative(0, Math.Max(1, p0)); break;     // HPR
            case 'b': // REP - repeat last printed character
                if (_lastPrintedCodepoint != 0)
                {
                    int repCount = Math.Max(1, p0);
                    for (int i = 0; i < repCount; i++)
                        _buffer.WriteCodepoint(_lastPrintedCodepoint, _lastPrintedWide);
                }
                break;
            case 'd': _buffer.SetCursorRow(Math.Max(1, p0) - 1); break;          // VPA
            case 'e': _buffer.MoveCursorRelative(Math.Max(1, p0), 0); break;     // VPR
            case 'g': _buffer.ClearTabStop(p0); break;                           // TBC
            case 'm': DispatchSgr(); break;                                       // SGR
            case 'n': // DSR — Device Status Report (respond with position)
                break; // ignore — we're headless
            case 'r': // DECSTBM — set scrolling region
                if (_paramCount >= 2)
                    _buffer.SetScrollRegion(Math.Max(1, _params[0]) - 1, Math.Max(1, _params[1]) - 1);
                else
                    _buffer.SetScrollRegion(0, _buffer.Rows - 1);
                break;
            case 's': _buffer.SaveCursor(); break;
            case 'u': _buffer.RestoreCursor(); break;
            case 't': DispatchWindowOp(p0, p1); break;
            case 'c': break; // DA — ignore
            case 'h': break; // SM  — ignore non-private modes
            case 'l': break; // RM  — ignore non-private modes
            case 'q': // DECSCUSR cursor shape: CSI Ps SP q (intermediate = space 0x20)
                if (intermediate == (byte)' ') _buffer.SetCursorShape(p0);
                break;
            case '`': _buffer.SetCursorCol(Math.Max(1, p0) - 1); break;          // HPA
        }
    }

    private void DispatchCsiPrivate(byte finalByte, int mode)
    {
        bool enable = finalByte == (byte)'h';
        switch (mode)
        {
            case 1:    break; // DECCKM cursor keys
            case 3:    break; // DECCOLM 132-column
            case 4:    break; // DECSCLM smooth scroll
            case 5:    break; // DECSCNM reverse video
            case 6:    break; // DECOM origin mode
            case 7:    break; // DECAWM auto-wrap
            case 12:   break; // cursor blink
            case 25:   _buffer.SetCursorVisible(enable); break;
            case 47:   // alternate screen (old)
            case 1047: // alternate screen with clear (xterm)
                if (enable) _buffer.EnterAlternateScreen(); else _buffer.ExitAlternateScreen(); break;
            case 1000: break; // mouse tracking
            case 1002: break;
            case 1003: break;
            case 1004: break; // focus events
            case 1006: break; // SGR mouse
            case 1049: // alternate screen + save/restore cursor
                if (enable) { _buffer.SaveCursor(); _buffer.EnterAlternateScreen(); }
                else         { _buffer.ExitAlternateScreen(); _buffer.RestoreCursor(); }
                break;
            case 2004: break; // bracketed paste
            case 2026: _buffer.SetSyncOutput(enable); break; // synchronized output
            case 9001: break; // win32 input mode
        }
    }

    private void DispatchSgr()
    {
        // SGR with zero params means reset
        if (_paramCount == 0)
        {
            ResetSgr();
            return;
        }

        int i = 0;
        while (i < _paramCount)
        {
            int p = _params[i];
            switch (p)
            {
                case 0:  ResetSgr(); break;
                case 1:  _buffer.CurrentAttributes |= CellAttributes.Bold; break;
                case 2:  _buffer.CurrentAttributes |= CellAttributes.Dim; break;
                case 3:  _buffer.CurrentAttributes |= CellAttributes.Italic; break;
                case 4:  _buffer.CurrentAttributes |= CellAttributes.Underline; break;
                case 5:
                case 6:  _buffer.CurrentAttributes |= CellAttributes.Blink; break;
                case 7:  _buffer.CurrentAttributes |= CellAttributes.Inverse; break;
                case 8:  _buffer.CurrentAttributes |= CellAttributes.Invisible; break;
                case 9:  _buffer.CurrentAttributes |= CellAttributes.Strike; break;
                case 22: _buffer.CurrentAttributes &= ~(CellAttributes.Bold | CellAttributes.Dim); break;
                case 23: _buffer.CurrentAttributes &= ~CellAttributes.Italic; break;
                case 24: _buffer.CurrentAttributes &= ~CellAttributes.Underline; break;
                case 25: _buffer.CurrentAttributes &= ~CellAttributes.Blink; break;
                case 27: _buffer.CurrentAttributes &= ~CellAttributes.Inverse; break;
                case 28: _buffer.CurrentAttributes &= ~CellAttributes.Invisible; break;
                case 29: _buffer.CurrentAttributes &= ~CellAttributes.Strike; break;

                // Standard fg colors 30-37
                case 30: case 31: case 32: case 33:
                case 34: case 35: case 36: case 37:
                    _buffer.CurrentFg = CellColor.FromPalette((byte)(p - 30));
                    break;
                case 38: // extended fg
                    i = ParseExtendedColor(i, out var extFg);
                    _buffer.CurrentFg = extFg;
                    continue;
                case 39: _buffer.CurrentFg = CellColor.Default; break;

                // Standard bg colors 40-47
                case 40: case 41: case 42: case 43:
                case 44: case 45: case 46: case 47:
                    _buffer.CurrentBg = CellColor.FromPalette((byte)(p - 40));
                    break;
                case 48: // extended bg
                    i = ParseExtendedColor(i, out var extBg);
                    _buffer.CurrentBg = extBg;
                    continue;
                case 49: _buffer.CurrentBg = CellColor.Default; break;

                // Bright fg 90-97
                case 90: case 91: case 92: case 93:
                case 94: case 95: case 96: case 97:
                    _buffer.CurrentFg = CellColor.FromPalette((byte)(p - 90 + 8));
                    break;

                // Bright bg 100-107
                case 100: case 101: case 102: case 103:
                case 104: case 105: case 106: case 107:
                    _buffer.CurrentBg = CellColor.FromPalette((byte)(p - 100 + 8));
                    break;
            }
            i++;
        }
    }

    /// <summary>
    /// Parse 38;5;n (256-color) or 38;2;r;g;b (true color) starting at param index i.
    /// Returns the new index after consuming the color params.
    /// </summary>
    private int ParseExtendedColor(int i, out CellColor color)
    {
        color = CellColor.Default;
        int next = i + 1;
        if (next >= _paramCount) return next;

        int mode = _params[next];
        if (mode == 5 && next + 1 < _paramCount)
        {
            color = CellColor.FromPalette((byte)_params[next + 1]);
            return next + 2;
        }
        if (mode == 2 && next + 3 < _paramCount)
        {
            color = CellColor.FromRgb(
                (byte)_params[next + 1],
                (byte)_params[next + 2],
                (byte)_params[next + 3]);
            return next + 4;
        }
        return next + 1;
    }

    private void DispatchOsc()
    {
        // OSC format: "Ps;data" — we only care about title (0, 1, 2)
        // For our purposes (state proxy) we just ignore OSC content
        _oscLen = 0;
    }

    private void DispatchWindowOp(int op, int p1)
    {
        // CSI t — xterm window operations. Only handle resize (8;rows;cols t)
        if (op == 8 && _paramCount >= 3)
        {
            int rows = _params[1];
            int cols = _params[2];
            if (rows > 0 && cols > 0)
                _buffer.Resize(cols, rows);
        }
        // Everything else (iconify, maximize, etc.) — ignore
    }

    private void ResetSgr()
    {
        _buffer.CurrentFg = CellColor.Default;
        _buffer.CurrentBg = CellColor.Default;
        _buffer.CurrentAttributes = CellAttributes.None;
    }

    private void SoftReset()
    {
        ResetSgr();
        // Do NOT force cursor visible — DECSTR resets many things but must not
        // override a TUI's explicit ?25l. Forcing visible here causes the serializer
        // to replay a phantom cursor on reconnect when TUIs like Copilot use DECSTR.
        _buffer.SetScrollRegion(0, _buffer.Rows - 1);
    }

    private void FullReset()
    {
        ResetSgr();
        _buffer.MoveCursorTo(0, 0);
        _buffer.EraseInDisplay(2);
        _buffer.ExitAlternateScreen();
        _buffer.SetCursorVisible(true);
        _buffer.SetScrollRegion(0, _buffer.Rows - 1);
    }

    // ------------------------------------------------------------------
    // UTF-8 decoder — builds codepoints from raw bytes, then writes to buffer
    // ------------------------------------------------------------------

    private void ProcessUtf8(byte b)
    {
        if (_utf8Remaining > 0)
        {
            if ((b & 0xC0) != 0x80)
            {
                // Invalid continuation — reset and treat as new byte
                _utf8Remaining = 0;
                _utf8Codepoint = 0;
            }
            else
            {
                _utf8Codepoint = (_utf8Codepoint << 6) | (uint)(b & 0x3F);
                _utf8Remaining--;
                if (_utf8Remaining == 0)
                    EmitCodepoint(_utf8Codepoint);
                return;
            }
        }

        if (b < 0x80)
        {
            EmitCodepoint((uint)b);
        }
        else if ((b & 0xE0) == 0xC0)
        {
            _utf8Codepoint = (uint)(b & 0x1F);
            _utf8Remaining = 1;
        }
        else if ((b & 0xF0) == 0xE0)
        {
            _utf8Codepoint = (uint)(b & 0x0F);
            _utf8Remaining = 2;
        }
        else if ((b & 0xF8) == 0xF0)
        {
            _utf8Codepoint = (uint)(b & 0x07);
            _utf8Remaining = 3;
        }
        // else: invalid byte, skip
    }

    private void EmitCodepoint(uint cp)
    {
        if (cp < 0x20) return; // C0 controls already handled
        if (cp == 0x7F) return; // DEL

        bool wide = IsWideChar(cp);
        _buffer.WriteCodepoint((int)cp, wide);
        _lastPrintedCodepoint = (int)cp;
        _lastPrintedWide = wide;
    }

    /// <summary>
    /// Returns true for East Asian Wide characters (2-column width).
    /// Covers CJK Unified Ideographs, fullwidth forms, and common emoji ranges.
    /// </summary>
    private static bool IsWideChar(uint cp) =>
        (cp >= 0x1100 && cp <= 0x115F)  ||  // Hangul Jamo
        (cp >= 0x2E80 && cp <= 0x303E)  ||  // CJK Radicals
        (cp >= 0x3041 && cp <= 0x33FF)  ||  // Hiragana / Katakana / etc
        (cp >= 0x3400 && cp <= 0x4DBF)  ||  // CJK Extension A
        (cp >= 0x4E00 && cp <= 0x9FFF)  ||  // CJK Unified
        (cp >= 0xA000 && cp <= 0xA4CF)  ||  // Yi
        (cp >= 0xAC00 && cp <= 0xD7AF)  ||  // Hangul Syllables
        (cp >= 0xF900 && cp <= 0xFAFF)  ||  // CJK Compatibility
        (cp >= 0xFE10 && cp <= 0xFE1F)  ||  // Vertical forms
        (cp >= 0xFE30 && cp <= 0xFE6F)  ||  // CJK Compatibility Forms
        (cp >= 0xFF01 && cp <= 0xFF60)  ||  // Fullwidth Forms
        (cp >= 0xFFE0 && cp <= 0xFFE6)  ||  // Fullwidth Signs
        (cp >= 0x1F300 && cp <= 0x1F64F) || // Misc Symbols, Emoticons
        (cp >= 0x1F900 && cp <= 0x1F9FF) || // Supplemental Symbols
        (cp >= 0x20000 && cp <= 0x2FFFD) || // CJK Extension B-F
        (cp >= 0x30000 && cp <= 0x3FFFD);   // CJK Extension G+

    private enum ParserState : byte
    {
        Ground,
        Escape,
        EscapeIntermediate,
        CsiEntry,
        CsiParam,
        CsiIntermediate,
        OscString,
        DcsPassthrough,
    }
}
