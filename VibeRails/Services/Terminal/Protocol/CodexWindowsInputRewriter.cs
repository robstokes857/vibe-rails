using System.Buffers;
using System.Text;

namespace VibeRails.Services.Terminal;

/// <summary>
/// Rewrites Codex's logical bare-LF insert-newline action into a ConPTY
/// win32-input Shift+Enter record. One instance belongs to one terminal because
/// CRLF and bracketed-paste boundaries can be split across input writes.
/// </summary>
public sealed class CodexWindowsInputRewriter
{
    public const string Win32ShiftEnterDown = "\u001b[13;28;0;1;16;1_";

    private static readonly byte[] s_win32ShiftEnterDownBytes =
        Encoding.ASCII.GetBytes(Win32ShiftEnterDown);

    private PasteParseState _pasteParseState;
    private int _csiParameter;
    private int _csiDigitCount;
    private bool _csiHasOnlyDigits;
    private bool _inBracketedPaste;
    private bool _previousWasCarriageReturn;

    public static bool ShouldRewrite(LLM llm) =>
        OperatingSystem.IsWindows() && llm == LLM.Codex;

    /// <summary>
    /// Rewrites only bare <c>0x0A</c> bytes outside bracketed paste. All other
    /// bytes are preserved exactly, including invalid or split UTF-8. Parser
    /// state is retained across calls; the owning <see cref="Terminal"/> must
    /// serialize this state transition with the corresponding PTY write.
    /// </summary>
    public ReadOnlyMemory<byte> Rewrite(ReadOnlyMemory<byte> input)
    {
        if (input.IsEmpty)
            return input;

        ArrayBufferWriter<byte>? output = null;
        var bytes = input.Span;

        for (var i = 0; i < bytes.Length; i++)
        {
            var value = bytes[i];
            UpdatePasteState(value);

            var rewrite = value == (byte)'\n'
                && !_inBracketedPaste
                && !_previousWasCarriageReturn;

            if (rewrite)
            {
                output ??= StartOutput(bytes, i);
                Append(output, s_win32ShiftEnterDownBytes);
            }
            else if (output is not null)
            {
                Append(output, value);
            }

            _previousWasCarriageReturn = value == (byte)'\r';
        }

        // Returning the caller's original memory is deliberate: when no LF was
        // rewritten, Terminal can forward the exact raw bytes without a copy or
        // any UTF-8 decode/re-encode round trip.
        return output is null ? input : output.WrittenMemory;
    }

    /// <summary>
    /// Breaks byte adjacency when an out-of-band/raw write is inserted into the
    /// same PTY stream. A raw write invalidates a partial CSI marker and a prior
    /// CR, but it does not end an already-established bracketed paste.
    /// </summary>
    public void BreakInputSequence()
    {
        ResetCsiParser();
        _previousWasCarriageReturn = false;
    }

    private void UpdatePasteState(byte value)
    {
        switch (_pasteParseState)
        {
            case PasteParseState.None:
                if (value == 0x1B)
                    _pasteParseState = PasteParseState.AfterEscape;
                return;

            case PasteParseState.AfterEscape:
                if (value == (byte)'[')
                {
                    _pasteParseState = PasteParseState.Csi;
                    _csiParameter = 0;
                    _csiDigitCount = 0;
                    _csiHasOnlyDigits = true;
                }
                else
                {
                    // A second ESC can itself begin the next candidate marker.
                    _pasteParseState = value == 0x1B
                        ? PasteParseState.AfterEscape
                        : PasteParseState.None;
                }
                return;

            case PasteParseState.Csi:
                if (value == 0x1B)
                {
                    _pasteParseState = PasteParseState.AfterEscape;
                    _csiParameter = 0;
                    _csiDigitCount = 0;
                    _csiHasOnlyDigits = false;
                    return;
                }

                if (value is >= 0x40 and <= 0x7E)
                {
                    if (value == (byte)'~'
                        && _csiHasOnlyDigits
                        && _csiDigitCount == 3)
                    {
                        if (_csiParameter == 200)
                            _inBracketedPaste = true;
                        else if (_csiParameter == 201)
                            _inBracketedPaste = false;
                    }

                    ResetCsiParser();
                    return;
                }

                if (value is >= (byte)'0' and <= (byte)'9' && _csiHasOnlyDigits)
                {
                    // Only 200/201 are meaningful. Stop accepting an oversized
                    // parameter rather than risk integer overflow on hostile input.
                    if (_csiDigitCount < 3)
                    {
                        _csiParameter = (_csiParameter * 10) + value - (byte)'0';
                        _csiDigitCount++;
                    }
                    else
                    {
                        _csiHasOnlyDigits = false;
                    }
                }
                else
                {
                    _csiHasOnlyDigits = false;
                }
                return;
        }
    }

    private void ResetCsiParser()
    {
        _pasteParseState = PasteParseState.None;
        _csiParameter = 0;
        _csiDigitCount = 0;
        _csiHasOnlyDigits = false;
    }

    private static ArrayBufferWriter<byte> StartOutput(ReadOnlySpan<byte> input, int prefixLength)
    {
        var output = new ArrayBufferWriter<byte>(
            input.Length + s_win32ShiftEnterDownBytes.Length - 1);
        Append(output, input[..prefixLength]);
        return output;
    }

    private static void Append(ArrayBufferWriter<byte> output, ReadOnlySpan<byte> bytes)
    {
        bytes.CopyTo(output.GetSpan(bytes.Length));
        output.Advance(bytes.Length);
    }

    private static void Append(ArrayBufferWriter<byte> output, byte value)
    {
        output.GetSpan(1)[0] = value;
        output.Advance(1);
    }

    private enum PasteParseState
    {
        None = 0,
        AfterEscape = 1,
        Csi = 2
    }
}
