using System.Text;
using System.Threading.Channels;

namespace VibeRails.Utils;

/// <summary>
/// Accumulates keystrokes from the terminal and fires a callback when the user presses Enter.
/// Designed for command-line logging: completed lines only, with control/escape
/// sequences filtered so cursor navigation/edit keys do not pollute captured text.
/// </summary>
public sealed class InputAccumulator : IAsyncDisposable, IDisposable
{
    private const int MaxLineLength = 8 * 1024;
    private const char Escape = '\x1B';

    private readonly StringBuilder _lineBuffer = new();
    private readonly Func<string, Task> _onInputComplete;
    private readonly object _lock = new();
    private readonly Channel<string> _completedLines = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly Task _worker;

    private EscapeParseState _escapeState = EscapeParseState.None;
    private bool _escapeStringSawEsc;
    private bool _disposed;

    public InputAccumulator(Func<string, Task> onInputComplete)
    {
        _onInputComplete = onInputComplete ?? throw new ArgumentNullException(nameof(onInputComplete));
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
                // First consume any active escape sequence state.
                if (ConsumeEscapeSequenceChar(c))
                    continue;

                if (c == Escape)
                {
                    _escapeState = EscapeParseState.AfterEsc;
                    continue;
                }

                if (c == '\r' || c == '\n')
                {
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
            _escapeState = EscapeParseState.None;
            _escapeStringSawEsc = false;
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
                        Console.Error.WriteLine($"[VibeRails] Error in input callback: {ex.Message}");
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

    private bool ConsumeEscapeSequenceChar(char c)
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
                _escapeStringSawEsc = false;
                return true;

            case EscapeParseState.Csi:
                if (IsEscapeFinalByte(c))
                    _escapeState = EscapeParseState.None;
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
