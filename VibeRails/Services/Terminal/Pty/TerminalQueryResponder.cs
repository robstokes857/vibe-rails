using System.Text;
using System.Threading.Channels;
using Serilog;

namespace VibeRails.Services.Terminal;

/// <summary>
/// What the headless emulator holds at the point in the stream where a query was asked.
/// </summary>
/// <param name="CursorRow">Zero-based cursor row.</param>
/// <param name="CursorCol">Zero-based cursor column.</param>
/// <param name="ModeStatus">For a mode query, the DECRPM status of the mode asked about.</param>
internal readonly record struct TerminalQueryState(
    int CursorRow, int CursorCol, int Rows, int Cols, int ModeStatus = TerminalModeReport.NotRecognized);

/// <summary>
/// DECRPM status values, and the state of the modes the headless emulator tracks. A mode it does
/// not track is reported as not recognized, which is always safe: a program then leaves the
/// feature alone. The permanent values are the ones xterm.js 6 reports for the same modes.
/// </summary>
/// <remarks>
/// Cursor visibility (mode 25) is deliberately not reported. The emulator starts with the cursor
/// hidden and only learns otherwise from an explicit <c>?25h</c>, which a pass-through host never
/// sends on a program's behalf. Reporting "hidden" at startup would invite a program to restore
/// that state on exit and leave the shell without a cursor.
/// </remarks>
internal static class TerminalModeReport
{
    internal const int NotRecognized = 0;
    internal const int Set = 1;
    internal const int Reset = 2;
    internal const int PermanentlySet = 3;
    internal const int PermanentlyReset = 4;

    /// <summary>The status of a DEC private mode (<c>CSI ? Ps $ p</c>).</summary>
    internal static int PrivateMode(TerminalEmulator.Terminal emulator, int mode) => mode switch
    {
        47 or 1047 or 1049 => Of(emulator.IsAlternateScreen),
        9 or 1000 or 1002 or 1003 or 1004 or 1006 =>
            Of(Array.IndexOf(emulator.GetInputReportingModes(), mode) >= 0),
        2004 => Of(emulator.BracketedPasteActive),
        // Synchronized output: xterm.js honors it, and under a pass-through host the brackets
        // reach it as the program wrote them.
        2026 => Of(emulator.SyncOutputActive),
        8 => PermanentlySet,
        67 or 1005 or 1015 => PermanentlyReset,
        1048 => Set,
        _ => NotRecognized,
    };

    /// <summary>The status of an ANSI mode (<c>CSI Ps $ p</c>). None of them is tracked.</summary>
    internal static int AnsiMode(int mode) => mode switch
    {
        2 => PermanentlyReset,
        12 => PermanentlySet,
        _ => NotRecognized,
    };

    private static int Of(bool on) => on ? Set : Reset;
}

/// <summary>
/// Answers the terminal queries a pass-through console host forwards: device attributes, device
/// status, cursor position and mode state. One of these belongs to one terminal whose PTY runs
/// under a bundled conpty.dll.
///
/// <para>
/// The server answers, not an attached viewer, for three measured reasons (conpty 1.25.2611):
/// the host asks for device attributes at startup, before any viewer exists, and swallows the
/// first answer it ever receives; after every resize it asks for the cursor position and each
/// console call that reads the cursor waits up to 500 ms for it; and a tab may have no viewer,
/// or a hidden one whose timers are throttled. An unanswered host query is not harmless: the
/// program's next answer is consumed by the host in its place.
/// </para>
///
/// <para>
/// There must be exactly one responder, and it must answer in the order the program asked. A
/// second device-attributes answer reaches the program as the typed characters
/// <c>ESC [ ? 1 ; 2 c</c>, and a cursor report nobody asked for is an F3 keypress. An answer from
/// a viewer would also arrive after this one's, and programs use a device-attributes or cursor
/// query as the marker that ends a batch of optional queries: anything later is stray input.
/// Viewers of such a terminal therefore answer no terminal query at all (see
/// <c>terminal-device-queries.js</c>). A query nobody answers here is simply unsupported.
/// </para>
///
/// <para>
/// Replies are terminal-generated input. They are queued here and written in order by one
/// pump, never on the read loop: the host may be blocked writing output while we would be
/// blocked writing input. They do not pass through <c>TerminalIoRouter</c>, so they are not
/// recorded as something the user typed.
/// </para>
/// </summary>
internal sealed class TerminalQueryResponder
{
    /// <summary>
    /// Replies waiting to be written. A program that floods queries while the host is not
    /// reading input must not grow this without bound; past the cap a query goes unanswered.
    /// </summary>
    internal const int MaxPendingReplies = 256;

    // What xterm.js, the terminal that renders these sessions, reports about itself.
    private static readonly byte[] s_primaryDeviceAttributes = "\u001b[?1;2c"u8.ToArray();
    private static readonly byte[] s_secondaryDeviceAttributes = "\u001b[>0;276;0c"u8.ToArray();
    private static readonly byte[] s_operatingStatusOk = "\u001b[0n"u8.ToArray();

    private readonly Func<ReadOnlyMemory<byte>, CancellationToken, Task> _write;
    private readonly CancellationToken _stopped;
    private readonly Channel<byte[]> _replies = Channel.CreateUnbounded<byte[]>(
        new UnboundedChannelOptions { SingleReader = true });
    private int _pending;
    private int _pumpStarted;
    private int _stoppedWriting;
    private long _answered;
    private long _dropped;

    /// <param name="write">Writes raw bytes to the PTY input, serialized with all other input.</param>
    /// <param name="stopped">Cancelled when the terminal is disposed.</param>
    public TerminalQueryResponder(
        Func<ReadOnlyMemory<byte>, CancellationToken, Task> write,
        CancellationToken stopped)
    {
        _write = write;
        _stopped = stopped;
    }

    /// <summary>Queries answered so far.</summary>
    internal long AnsweredCount => Interlocked.Read(ref _answered);

    /// <summary>Queries left unanswered because too many replies were already waiting.</summary>
    internal long DroppedCount => Interlocked.Read(ref _dropped);

    /// <summary>
    /// Queues the answer to <paramref name="query"/>. Called on the read loop with the emulator's
    /// state as it stands immediately after the query, so a cursor report describes the same
    /// point in the stream the program asked at. Never blocks.
    /// </summary>
    /// <param name="parameter">The query's first parameter: the mode number of a mode query.</param>
    public void Answer(TerminalQuery query, int parameter, in TerminalQueryState state)
    {
        var reply = BuildReply(query, parameter, state);
        if (reply is null || Volatile.Read(ref _stoppedWriting) != 0)
            return;

        if (Interlocked.Increment(ref _pending) > MaxPendingReplies)
        {
            Interlocked.Decrement(ref _pending);
            if (Interlocked.Increment(ref _dropped) == 1)
                Log.Warning("[Terminal] Terminal query left unanswered: {Pending} replies are already waiting for the console host to read input", MaxPendingReplies);
            return;
        }

        _replies.Writer.TryWrite(reply);
        if (Interlocked.Exchange(ref _pumpStarted, 1) == 0)
            _ = Task.Run(PumpAsync);
    }

    /// <summary>The bytes that answer a query, or null when the server does not answer it.</summary>
    internal static byte[]? BuildReply(TerminalQuery query, int parameter, in TerminalQueryState state)
    {
        switch (query)
        {
            case TerminalQuery.PrimaryDeviceAttributes:
                return s_primaryDeviceAttributes;
            case TerminalQuery.SecondaryDeviceAttributes:
                return s_secondaryDeviceAttributes;
            case TerminalQuery.OperatingStatus:
                return s_operatingStatusOk;
            case TerminalQuery.CursorPosition:
            case TerminalQuery.ExtendedCursorPosition:
                // One-based, and always inside the grid: the host adopts the position it is told.
                var row = Math.Clamp(state.CursorRow, 0, Math.Max(0, state.Rows - 1)) + 1;
                var col = Math.Clamp(state.CursorCol, 0, Math.Max(0, state.Cols - 1)) + 1;
                var marker = query == TerminalQuery.ExtendedCursorPosition ? "?" : "";
                return Encoding.ASCII.GetBytes($"\u001b[{marker}{row};{col}R");
            case TerminalQuery.PrivateMode:
                return Encoding.ASCII.GetBytes($"\u001b[?{parameter};{state.ModeStatus}$y");
            case TerminalQuery.AnsiMode:
                return Encoding.ASCII.GetBytes($"\u001b[{parameter};{state.ModeStatus}$y");
            default:
                return null;
        }
    }

    private async Task PumpAsync()
    {
        try
        {
            await foreach (var reply in _replies.Reader.ReadAllAsync(_stopped))
            {
                Interlocked.Decrement(ref _pending);
                await _write(reply, _stopped);
                Interlocked.Increment(ref _answered);
            }
        }
        catch (OperationCanceledException)
        {
            // The terminal was disposed.
        }
        catch (Exception ex)
        {
            // The PTY input is gone (the process exited or the pipe closed). Nothing is left to answer.
            Log.Debug(ex, "[Terminal] Terminal query replies stopped");
        }
        finally
        {
            Volatile.Write(ref _stoppedWriting, 1);
        }
    }
}
