namespace VibeRails.Services.Terminal;

/// <summary>A terminal query the server answers for a pass-through console host.</summary>
internal enum TerminalQuery
{
    None = 0,

    /// <summary><c>CSI c</c> / <c>CSI 0 c</c> — primary device attributes (DA1).</summary>
    PrimaryDeviceAttributes,

    /// <summary><c>CSI &gt; c</c> / <c>CSI &gt; 0 c</c> — secondary device attributes (DA2).</summary>
    SecondaryDeviceAttributes,

    /// <summary><c>CSI 5 n</c> — operating status (DSR).</summary>
    OperatingStatus,

    /// <summary><c>CSI 6 n</c> — cursor position report (CPR).</summary>
    CursorPosition,

    /// <summary><c>CSI ? 6 n</c> — extended cursor position report (DECXCPR).</summary>
    ExtendedCursorPosition,

    /// <summary><c>CSI ? Ps $ p</c> — the state of a DEC private mode (DECRQM).</summary>
    PrivateMode,

    /// <summary><c>CSI Ps $ p</c> — the state of an ANSI mode (DECRQM).</summary>
    AnsiMode,
}

/// <summary>
/// Finds device-attribute, device-status and mode queries in PTY output. It reads the stream and
/// changes nothing in it: every byte still goes to the emulator and to every viewer.
///
/// <para>
/// Only a pass-through console host needs this. The ConPTY built into Windows answers these
/// queries itself and never forwards them; a bundled conpty.dll forwards all of them (measured on
/// 1.25.2611: <c>CSI 5n</c>, <c>6n</c>, <c>?6n</c>, <c>c</c>, <c>&gt;c</c>, <c>?2026$p</c>) and
/// asks two of its own, device attributes at startup and the cursor position after every resize.
/// </para>
///
/// <para>
/// A byte-wise state machine, so a query split across two reads is still found. It follows the
/// same string and escape boundaries as <c>TerminalEmulator.AnsiParser</c> and xterm.js: text
/// inside an OSC or DCS string is never a query, and ESC always starts over.
/// </para>
/// </summary>
internal struct TerminalQueryScanner
{
    /// <summary>The largest mode number reported; anything longer is not a query we answer.</summary>
    internal const int MaxParameter = 9999;

    private const byte Esc = 0x1B;
    private const byte Malformed = 0xFF;

    private State _state;
    private byte _prefix;
    private byte _intermediate;
    private bool _atStart;
    private bool _firstParameterDone;
    private int _firstParameter;

    private enum State : byte
    {
        Ground,
        Escape,
        Csi,
        OscString,
        StringPassthrough,
    }

    /// <summary>
    /// Reads <paramref name="data"/> up to and including the final byte of the next query.
    /// Returns how many bytes that was; <paramref name="query"/> is <see cref="TerminalQuery.None"/>
    /// when the span ended first, and the whole span was read.
    /// </summary>
    /// <param name="parameter">The query's first parameter: the mode number of a mode query.</param>
    public int Scan(ReadOnlySpan<byte> data, out TerminalQuery query, out int parameter)
    {
        for (int i = 0; i < data.Length; i++)
        {
            query = Next(data[i]);
            if (query != TerminalQuery.None)
            {
                parameter = _firstParameter;
                return i + 1;
            }
        }

        query = TerminalQuery.None;
        parameter = 0;
        return data.Length;
    }

    private TerminalQuery Next(byte b)
    {
        switch (_state)
        {
            case State.Ground:
                if (b == Esc) _state = State.Escape;
                return TerminalQuery.None;

            case State.Escape:
                _state = b switch
                {
                    (byte)'[' => BeginCsi(),
                    (byte)']' => State.OscString,
                    (byte)'P' or (byte)'X' or (byte)'^' or (byte)'_' => State.StringPassthrough,
                    Esc => State.Escape,
                    _ => State.Ground,
                };
                return TerminalQuery.None;

            case State.Csi:
                return NextCsi(b);

            case State.OscString:
                // BEL ends an OSC; ESC ends it too and may begin ST or a new sequence.
                if (b == 0x07 || b is 0x18 or 0x1A) _state = State.Ground;
                else if (b == Esc) _state = State.Escape;
                return TerminalQuery.None;

            default:
                if (b is 0x18 or 0x1A) _state = State.Ground;
                else if (b == Esc) _state = State.Escape;
                return TerminalQuery.None;
        }
    }

    private State BeginCsi()
    {
        _prefix = 0;
        _intermediate = 0;
        _atStart = true;
        _firstParameterDone = false;
        _firstParameter = 0;
        return State.Csi;
    }

    private TerminalQuery NextCsi(byte b)
    {
        if (b == Esc)
        {
            _state = State.Escape;
            return TerminalQuery.None;
        }

        // CAN and SUB abort the sequence.
        if (b is 0x18 or 0x1A)
        {
            _state = State.Ground;
            return TerminalQuery.None;
        }

        // C0 controls act without ending the sequence, and DEL is ignored inside one.
        if (b < 0x20 || b == 0x7F)
            return TerminalQuery.None;

        bool first = _atStart;
        _atStart = false;

        if (b is >= (byte)'0' and <= (byte)'9')
        {
            // Parameters end where intermediates begin; a digit after one is malformed.
            if (_intermediate != 0)
                _intermediate = Malformed;
            else if (!_firstParameterDone && _firstParameter <= MaxParameter)
                _firstParameter = (_firstParameter * 10) + (b - (byte)'0');
            return TerminalQuery.None;
        }

        if (b is (byte)';' or (byte)':')
        {
            _firstParameterDone = true;
            return TerminalQuery.None;
        }

        // A private marker (< = > ?) is only one directly after CSI. Anywhere else the sequence
        // is malformed and never one of ours.
        if (b is >= 0x3C and <= 0x3F)
        {
            _prefix = first ? b : Malformed;
            return TerminalQuery.None;
        }

        if (b is >= 0x20 and <= 0x2F)
        {
            _intermediate = _intermediate == 0 ? b : Malformed;
            return TerminalQuery.None;
        }

        // Everything else ends the sequence: a final byte (0x40-0x7E), or a byte that cannot be
        // part of one, which aborts it.
        _state = State.Ground;
        if (_firstParameter > MaxParameter)
            return TerminalQuery.None;

        return (b, _prefix, _intermediate, _firstParameter) switch
        {
            ((byte)'c', 0, 0, 0) => TerminalQuery.PrimaryDeviceAttributes,
            ((byte)'c', (byte)'>', 0, 0) => TerminalQuery.SecondaryDeviceAttributes,
            ((byte)'n', 0, 0, 5) => TerminalQuery.OperatingStatus,
            ((byte)'n', 0, 0, 6) => TerminalQuery.CursorPosition,
            ((byte)'n', (byte)'?', 0, 6) => TerminalQuery.ExtendedCursorPosition,
            ((byte)'p', (byte)'?', (byte)'$', _) => TerminalQuery.PrivateMode,
            ((byte)'p', 0, (byte)'$', _) => TerminalQuery.AnsiMode,
            _ => TerminalQuery.None,
        };
    }
}
