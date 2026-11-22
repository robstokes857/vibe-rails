namespace VibeRails.Services.Terminal;

/// <summary>
/// Tracks whether the output fed to the headless emulator so far ends between two sequences: not
/// inside an escape sequence, not inside a string (OSC, DCS and the like) and not inside a UTF-8
/// character (VIBE-354).
/// </summary>
/// <remarks>
/// A PTY read can end anywhere, also in the middle of <c>ESC [ 3 1 m</c>. The emulator keeps the
/// half it has and finishes the sequence with the next read. Anything written to the emulator in
/// between ends that sequence, and its second half is then read as text. The emulator does not
/// expose its parser state, so this mirrors the state changes of
/// <c>TerminalEmulator.AnsiParser</c>, and only those: which bytes it acts on is not this type's
/// business. <c>EmulatorParseBoundaryTests</c> compares the two on split sequences.
/// </remarks>
internal struct EmulatorParseBoundary
{
    private const byte Esc = 0x1B;
    private const byte Bel = 0x07;

    private State _state;
    private byte _utf8Remaining;

    private enum State : byte
    {
        Ground,
        Escape,
        EscapeIntermediate,
        CsiEntry,
        Csi,
        String,
    }

    /// <summary>True when bytes written to the emulator now would not land inside a sequence.</summary>
    public readonly bool IsAtBoundary => _state == State.Ground && _utf8Remaining == 0;

    /// <summary>Accounts for bytes that have been, or are about to be, fed to the emulator.</summary>
    public void Advance(ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
            Next(b);
    }

    private void Next(byte b)
    {
        // Outside a string a C0 control acts at once and leaves the sequence in progress alone,
        // except ESC, which starts a new one. A half-read UTF-8 character survives both.
        if (b < 0x20 && _state != State.String)
        {
            if (b == Esc)
                _state = State.Escape;
            return;
        }

        switch (_state)
        {
            case State.Ground:
                if (_utf8Remaining > 0)
                {
                    if ((b & 0xC0) == 0x80)
                    {
                        _utf8Remaining--;
                        return;
                    }

                    _utf8Remaining = 0; // Not a continuation: the character is dropped and this byte starts over.
                }

                if ((b & 0xE0) == 0xC0) _utf8Remaining = 1;
                else if ((b & 0xF0) == 0xE0) _utf8Remaining = 2;
                else if ((b & 0xF8) == 0xF0) _utf8Remaining = 3;
                break;

            case State.Escape:
                _state = b switch
                {
                    >= 0x20 and <= 0x2F => State.EscapeIntermediate,
                    (byte)'[' => State.CsiEntry,
                    (byte)']' or (byte)'P' or (byte)'X' or (byte)'^' or (byte)'_' => State.String,
                    _ => State.Ground,
                };
                break;

            case State.EscapeIntermediate:
                // ESC ( B and its kind: one or more intermediates, then a final byte.
                if (b is >= 0x30 and <= 0x7E)
                    _state = State.Ground;
                break;

            case State.CsiEntry:
            case State.Csi:
                if (b is (>= 0x30 and <= 0x3B) or (>= 0x20 and <= 0x2F))
                    _state = State.Csi; // parameters, separators and intermediates
                else if (b is >= 0x3C and <= 0x3F && _state == State.CsiEntry)
                    _state = State.Csi; // a private marker, only directly after CSI
                else
                    _state = State.Ground; // a final byte, or a byte that aborts the sequence
                break;

            default:
                // A string runs to BEL, or to ESC (which normally begins ST).
                if (b == Bel) _state = State.Ground;
                else if (b == Esc) _state = State.Escape;
                break;
        }
    }
}
