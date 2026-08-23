using System.Text;
using System.Threading.Channels;
using Serilog;

namespace VibeRails.Utils;

/// <summary>
/// Accumulates keystrokes from the terminal and fires a callback when the user presses Enter.
/// Designed for command-line logging: completed lines only, with control/escape
/// sequences filtered so cursor navigation/edit keys do not pollute captured text.
///
/// Bracketed-paste support: when the CSI parser sees <c>ESC[200~</c>, subsequent
/// embedded newlines are buffered as literal characters instead of terminating a line.
/// When <c>ESC[201~</c> arrives, paste mode closes but the buffer is NOT flushed —
/// the pasted content merges with surrounding keystrokes and is only released when
/// the user presses Enter outside paste mode. This keeps TUI-driven inline pastes
/// (e.g. Claude Code's @-file autocomplete, which pastes the selected path into the
/// middle of the prompt) from producing premature <c>UserInputs</c> rows.
/// </summary>
public sealed class InputAccumulator : IAsyncDisposable, IDisposable
{
    private const int MaxLineLength = 8 * 1024;
    private const char Escape = '\x1B';

    private readonly StringBuilder _lineBuffer = new();
    private readonly StringBuilder _csiParams = new();
    private readonly Func<string, Task> _onInputComplete;
    private readonly bool _lineFeedInsertsNewline;
    private readonly object _lock = new();
    private readonly Channel<string> _completedLines = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly Task _worker;

    private EscapeParseState _escapeState = EscapeParseState.None;
    private bool _escapeStringSawEsc;
    private bool _inBracketedPaste;
    private bool _coalesceLineFeedAfterCarriageReturn;
    private bool _disposed;

    public InputAccumulator(
        Func<string, Task> onInputComplete,
        bool lineFeedInsertsNewline = false)
    {
        _onInputComplete = onInputComplete ?? throw new ArgumentNullException(nameof(onInputComplete));
        _lineFeedInsertsNewline = lineFeedInsertsNewline;
        _worker = Task.Run(ProcessCompletedLinesAsync);
    }

    /// <summary>
    /// Appends input from the terminal stream.
    /// </summary>
    public void Append(string input)
    {
        if (string.IsNullOrEmpty(input))
            return;

        var completed = new List<string>();
        lock (_lock)
        {
            if (_disposed)
                return;

            foreach (var c in input)
            {
                // Codex submit is CR while its modified-Enter/newline action is LF. If a
                // transport gives us a conventional CRLF submit, consume the LF as part of
                // that same submit instead of leaving it at the start of the next prompt.
                // This state intentionally survives Append calls because WebSocket/PTY
                // chunking may split the two bytes.
                if (_coalesceLineFeedAfterCarriageReturn)
                {
                    _coalesceLineFeedAfterCarriageReturn = false;
                    if (c == '\n')
                        continue;
                }

                // First consume any active escape sequence state. ConsumeEscapeSequenceChar
                // may flip _inBracketedPaste on ESC[200~ / ESC[201~ and enqueue a completed
                // line when a paste block closes.
                if (ConsumeEscapeSequenceChar(c, completed))
                    continue;

                if (c == Escape)
                {
                    _escapeState = EscapeParseState.AfterEsc;
                    continue;
                }

                if (c == '\r' || c == '\n')
                {
                    if (c == '\r' && _lineFeedInsertsNewline)
                        _coalesceLineFeedAfterCarriageReturn = true;

                    if (_inBracketedPaste)
                    {
                        // Inside a paste — preserve the newline as a literal character so the
                        // whole pasted block becomes one logical line. Normalize \r to \n.
                        TryAppend('\n');

                        // Safety cap: if a paste never closes (dropped ESC[201~), force-flush
                        // once the buffer exceeds MaxLineLength and drop out of paste mode.
                        if (_lineBuffer.Length >= MaxLineLength)
                        {
                            var forced = _lineBuffer.ToString();
                            _lineBuffer.Clear();
                            _inBracketedPaste = false;
                            if (!string.IsNullOrEmpty(forced))
                                completed.Add(forced);
                        }
                        continue;
                    }

                    // VibeRails represents Codex's modified-Enter action as LF for
                    // input history, while CR remains plain Enter / submit. On Windows,
                    // TerminalIoRouter rewrites the PTY-bound LF to a real ConPTY
                    // Shift+Enter record. Buffer the logical LF here so one multiline
                    // prompt remains one history row. Other CLIs retain the established
                    // behavior where either byte completes a line.
                    if (c == '\n' && _lineFeedInsertsNewline)
                    {
                        TryAppend('\n');
                        continue;
                    }

                    var completedLine = _lineBuffer.ToString();
                    _lineBuffer.Clear();
                    if (!string.IsNullOrEmpty(completedLine))
                        completed.Add(completedLine);
                }
                else if (c == '\x7F' || c == '\b')
                {
                    // Backspace - remove last character
                    if (_lineBuffer.Length > 0)
                    {
                        _lineBuffer.Length--;
                    }
                }
                else if (c == '\t')
                {
                    TryAppend('\t');
                }
                else if (c >= 32) // Printable characters only
                {
                    TryAppend(c);
                }
                // Ignore other control characters (Ctrl+C, Ctrl+D, etc.)
            }
        }

        foreach (var line in completed)
        {
            _completedLines.Writer.TryWrite(line);
        }
    }

    /// <summary>
    /// Gets the current buffer contents without clearing.
    /// </summary>
    public string CurrentBuffer
    {
        get
        {
            lock (_lock)
            {
                return _lineBuffer.ToString();
            }
        }
    }

    /// <summary>
    /// Clears the buffer without firing the callback.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _lineBuffer.Clear();
            _csiParams.Clear();
            _escapeState = EscapeParseState.None;
            _escapeStringSawEsc = false;
            _inBracketedPaste = false;
            _coalesceLineFeedAfterCarriageReturn = false;
        }
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
        }

        _completedLines.Writer.TryComplete();
        try
        {
            await _worker.ConfigureAwait(false);
        }
        catch { }
    }

    private async Task ProcessCompletedLinesAsync()
    {
        try
        {
            while (await _completedLines.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (_completedLines.Reader.TryRead(out var line))
                {
                    try
                    {
                        await _onInputComplete(line).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "[VibeRails] Error in input callback");
                    }
                }
            }
        }
        catch { }
    }

    private void TryAppend(char c)
    {
        if (_lineBuffer.Length < MaxLineLength)
        {
            _lineBuffer.Append(c);
        }
        // If the line exceeds MaxLineLength, silently truncate for safety.
    }

    private bool ConsumeEscapeSequenceChar(char c, List<string> completed)
    {
        switch (_escapeState)
        {
            case EscapeParseState.None:
                return false;

            case EscapeParseState.AfterEsc:
                if (c == Escape)
                {
                    _escapeState = EscapeParseState.AfterEsc;
                    return true;
                }

                _escapeState = c switch
                {
                    '[' => EscapeParseState.Csi,
                    'O' => EscapeParseState.Ss3,
                    ']' => EscapeParseState.Osc,
                    'P' or '^' or '_' => EscapeParseState.EscString,
                    _ => EscapeParseState.None
                };
                if (_escapeState == EscapeParseState.Csi)
                {
                    _csiParams.Clear();
                }
                _escapeStringSawEsc = false;
                return true;

            case EscapeParseState.Csi:
                if (IsEscapeFinalByte(c))
                {
                    // Bracketed-paste markers: ESC[200~ opens, ESC[201~ closes.
                    // Paste content is kept in the same line buffer; closing the paste does NOT
                    // flush. Callers that submit a paste standalone follow it with the session's
                    // submit byte (normally CR; LF also completes default-mode sessions). Callers
                    // that paste inline (autocomplete) keep typing, and the subsequent Enter still
                    // flushes the combined buffer as one line.
                    if (c == '~')
                    {
                        var parameters = _csiParams.ToString();
                        if (parameters == "200")
                        {
                            _inBracketedPaste = true;
                        }
                        else if (parameters == "201")
                        {
                            _inBracketedPaste = false;
                        }
                    }
                    _csiParams.Clear();
                    _escapeState = EscapeParseState.None;
                }
                else
                {
                    // Collect parameter/intermediate bytes so we can recognize the full sequence.
                    _csiParams.Append(c);
                }
                return true;

            case EscapeParseState.Ss3:
                if (IsEscapeFinalByte(c))
                    _escapeState = EscapeParseState.None;
                return true;

            case EscapeParseState.Osc:
            case EscapeParseState.EscString:
                if (c == '\a')
                {
                    _escapeState = EscapeParseState.None;
                    _escapeStringSawEsc = false;
                    return true;
                }

                if (_escapeStringSawEsc && c == '\\')
                {
                    _escapeState = EscapeParseState.None;
                    _escapeStringSawEsc = false;
                    return true;
                }

                _escapeStringSawEsc = c == Escape;
                return true;

            default:
                _escapeState = EscapeParseState.None;
                _escapeStringSawEsc = false;
                return false;
        }
    }

    private static bool IsEscapeFinalByte(char c) => c is >= '@' and <= '~';

    private enum EscapeParseState
    {
        None = 0,
        AfterEsc = 1,
        Csi = 2,
        Ss3 = 3,
        Osc = 4,
        EscString = 5
    }
}
