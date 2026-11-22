namespace VibeRails.Services.Terminal.Consumers;

/// <summary>
/// The emulator consumer for a terminal whose console host forwards terminal queries (a bundled
/// conpty.dll). It feeds the headless emulator exactly as <see cref="TerminalEmulatorConsumer"/>
/// does, and stops at each query to hand the responder the emulator's state at that point.
///
/// <para>
/// The split matters for the answer, not for the emulator: a program that writes text and a
/// cursor-position query in one chunk must be told where the cursor is after that text and before
/// whatever follows the query. Feeding the emulator in pieces yields the same grid as feeding it
/// whole; its parser is a byte-wise state machine.
/// </para>
/// </summary>
internal sealed class QueryAnsweringEmulatorConsumer : ITerminalConsumer
{
    private readonly TerminalEmulator.Terminal _emulator;
    private readonly Lock _lock;
    private readonly TerminalQueryResponder _responder;
    private TerminalQueryScanner _scanner;

    public QueryAnsweringEmulatorConsumer(
        TerminalEmulator.Terminal emulator,
        Lock emulatorLock,
        TerminalQueryResponder responder)
    {
        _emulator = emulator;
        _lock = emulatorLock;
        _responder = responder;
    }

    public void OnOutput(ReadOnlyMemory<byte> data)
    {
        var remaining = data.Span;
        lock (_lock)
        {
            while (!remaining.IsEmpty)
            {
                var read = _scanner.Scan(remaining, out var query, out var parameter);
                _emulator.Write(remaining[..read]);
                if (query != TerminalQuery.None)
                {
                    var modeStatus = query switch
                    {
                        TerminalQuery.PrivateMode => TerminalModeReport.PrivateMode(_emulator, parameter),
                        TerminalQuery.AnsiMode => TerminalModeReport.AnsiMode(parameter),
                        _ => TerminalModeReport.NotRecognized,
                    };
                    _responder.Answer(query, parameter, new TerminalQueryState(
                        _emulator.CursorRow, _emulator.CursorCol, _emulator.Rows, _emulator.Cols, modeStatus));
                }

                remaining = remaining[read..];
            }
        }
    }
}
