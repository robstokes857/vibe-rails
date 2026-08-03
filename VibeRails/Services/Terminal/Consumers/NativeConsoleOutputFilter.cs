using System.Text;

namespace VibeRails.Services.Terminal.Consumers;

/// <summary>
/// Filters PTY output on its way to the <b>real OS console</b> of a native session.
///
/// <para>
/// Background — the nested-terminal problem. A native session has two terminals: the outer console
/// window (conhost / Windows Terminal) that <c>vb</c> itself runs in, and the inner ConPTY that
/// <c>vb</c> creates for the CLI. VibeRails <b>is</b> the CLI's terminal, and it drives the inner
/// PTY's size from the outer console's size (<see cref="NativeConsoleGeometry"/>, polled every
/// 50 ms in <c>TerminalRunner.ConsoleInputLoopAsync</c>).
/// </para>
///
/// <para>
/// XTWINOPS (<c>CSI Ps ; Ps ; Ps t</c>) is how an application asks <i>its</i> terminal to resize or
/// move itself. Relaying that request to the <b>outer</b> console delivers it to the wrong terminal
/// and inverts the control loop: the app resizes the outer window, the geometry poll reads the new
/// size back, and pushes it into the inner PTY. Observed for real in session
/// <c>d2328427-187c-48ae-b0a5-2747a542fadf</c>, where opentui emitted <c>\e[8;41;156t</c> and
/// conhost obeyed it — 120x30 to 156x41 — and the console later snapped back to 120x30, reflowing
/// the alt-screen content it had already painted. opentui is a diff renderer, so it never repaints
/// those cells and the window stays shredded for the rest of the run. See TERMINAL.md
/// "## 2026-08-02 Automation runs in a native terminal…".
/// </para>
///
/// <para>
/// <b>This is the one deliberate exception to the no-stripping-the-byte-stream rule</b>, and it is
/// narrow on purpose: only the window <i>geometry</i> operations, only on the console relay. It is
/// not a rendering fix — it is refusing to misdeliver a control request addressed to us. Do not
/// generalise it to rendering sequences (SGR, DEC modes, cursor ops). The recording
/// (<c>DbLoggingConsumer</c>), the web viewer (<c>WebSocketConsumer</c>) and the remote viewer
/// (<c>RemoteOutputConsumer</c>) all keep receiving the unmodified stream.
/// </para>
/// </summary>
public sealed class NativeConsoleOutputFilter
{
    /// <summary>
    /// XTWINOPS operations that change the window's size or position — the ones that can desync the
    /// outer console from the inner PTY. Everything else (iconify, raise, lower, refresh, the
    /// <c>11..21</c> report queries, title-stack push/pop) is passed through untouched.
    /// </summary>
    private static readonly int[] WindowGeometryOps = [3, 4, 8, 9, 10];

    /// <summary>
    /// Upper bound on bytes held back waiting for a sequence to complete. A stray <c>ESC</c> that is
    /// never terminated must not stall console output forever, so past this the carry is flushed
    /// verbatim. Real XTWINOPS frames are well under 20 bytes.
    /// </summary>
    internal const int MaxCarryBytes = 64;

    private readonly Lock _lock = new();
    private byte[] _carry = [];

    /// <summary>Number of window-manipulation frames dropped so far, for logging.</summary>
    public int DroppedFrameCount { get; private set; }

    /// <summary>
    /// Returns the bytes that are safe to write to the console now. A trailing partial escape
    /// sequence (or partial UTF-8 character) is held back and prepended to the next call, so a
    /// sequence split across PTY reads is still recognised — and a multi-byte glyph split across
    /// reads is no longer decoded into two replacement characters.
    /// </summary>
    public byte[] Filter(ReadOnlySpan<byte> input)
    {
        lock (_lock)
        {
            var buffer = _carry.Length == 0 ? input.ToArray() : Concat(_carry, input);
            _carry = [];

            var output = new MemoryStream(buffer.Length);
            var emitFrom = 0;
            var i = 0;

            while (i < buffer.Length)
            {
                if (buffer[i] != 0x1B)
                {
                    i++;
                    continue;
                }

                // Lone trailing ESC — can't tell yet what it introduces.
                if (i + 1 >= buffer.Length)
                    break;

                // Only CSI is inspected. OSC and friends pass straight through; we never need to
                // reassemble them because we forward their bytes unchanged.
                if (buffer[i + 1] != (byte)'[')
                {
                    i += 2;
                    continue;
                }

                var paramsEnd = i + 2;
                while (paramsEnd < buffer.Length && buffer[paramsEnd] is >= 0x30 and <= 0x3F)
                    paramsEnd++;

                var finalIndex = paramsEnd;
                while (finalIndex < buffer.Length && buffer[finalIndex] is >= 0x20 and <= 0x2F)
                    finalIndex++;

                // Sequence is still arriving — carry it and wait for the rest.
                if (finalIndex >= buffer.Length)
                    break;

                var isPlainCsiT =
                    buffer[finalIndex] == (byte)'t'
                    && finalIndex == paramsEnd // no intermediates: excludes CSI Ps SP t (DECSWBV)
                    && IsPlainNumericParams(buffer, i + 2, paramsEnd);

                if (isPlainCsiT && IsWindowGeometryOp(buffer, i + 2, paramsEnd))
                {
                    output.Write(buffer, emitFrom, i - emitFrom);
                    emitFrom = finalIndex + 1;
                    DroppedFrameCount++;
                }

                // A byte outside the CSI final-byte range means the sequence was aborted, and the
                // byte that aborted it may itself start the next sequence — `ESC[12` followed by a
                // real `ESC[8;41;156t` is exactly that case. Resume *at* it rather than past it, or
                // the geometry request behind an aborted sequence is relayed to the console.
                // finalIndex >= i + 2, so this always makes progress.
                var isCsiFinalByte = buffer[finalIndex] is >= 0x40 and <= 0x7E;
                i = isCsiFinalByte ? finalIndex + 1 : finalIndex;
            }

            // `i` now marks either the end of the buffer or the start of an incomplete sequence.
            var carryFrom = i;

            // Nothing incomplete escape-wise: still avoid splitting a multi-byte UTF-8 character,
            // because the console relay decodes each write independently.
            if (carryFrom >= buffer.Length)
                carryFrom = buffer.Length - IncompleteUtf8TailLength(buffer);

            if (carryFrom < emitFrom)
                carryFrom = emitFrom;

            if (buffer.Length - carryFrom > MaxCarryBytes)
                carryFrom = buffer.Length; // never stall output on a malformed sequence

            output.Write(buffer, emitFrom, carryFrom - emitFrom);

            if (carryFrom < buffer.Length)
                _carry = buffer[carryFrom..];

            return output.ToArray();
        }
    }

    /// <summary>Flushes anything held back, for use when the session ends.</summary>
    public byte[] Drain()
    {
        lock (_lock)
        {
            var pending = _carry;
            _carry = [];
            return pending;
        }
    }

    private static bool IsPlainNumericParams(byte[] buffer, int start, int end)
    {
        for (var i = start; i < end; i++)
        {
            // Digits and ';' only — rejects the private forms (CSI ? … t, CSI > … t).
            if (buffer[i] is not ((>= (byte)'0' and <= (byte)'9') or (byte)';'))
                return false;
        }

        return true;
    }

    private static bool IsWindowGeometryOp(byte[] buffer, int start, int end)
    {
        var op = 0;
        var sawDigit = false;

        for (var i = start; i < end; i++)
        {
            if (buffer[i] == (byte)';')
                break;

            sawDigit = true;
            op = (op * 10) + (buffer[i] - (byte)'0');
            if (op > 1000)
                return false;
        }

        // `CSI t` with no parameter defaults to 0, which is not a geometry op.
        return sawDigit && Array.IndexOf(WindowGeometryOps, op) >= 0;
    }

    /// <summary>
    /// Length of a truncated UTF-8 character at the end of the buffer, or 0 when the tail is
    /// already complete.
    /// </summary>
    private static int IncompleteUtf8TailLength(byte[] buffer)
    {
        // A character is at most 4 bytes, so only the last 3 can be an incomplete lead+continuation.
        var scanFrom = Math.Max(0, buffer.Length - 3);

        for (var i = buffer.Length - 1; i >= scanFrom; i--)
        {
            var b = buffer[i];

            if ((b & 0b1100_0000) == 0b1000_0000)
                continue; // continuation byte — keep walking back to the lead

            var expected = b switch
            {
                >= 0xF0 => 4,
                >= 0xE0 => 3,
                >= 0xC0 => 2,
                _ => 1
            };

            var available = buffer.Length - i;
            return available < expected ? available : 0;
        }

        return 0;
    }

    private static byte[] Concat(byte[] first, ReadOnlySpan<byte> second)
    {
        var combined = new byte[first.Length + second.Length];
        first.CopyTo(combined, 0);
        second.CopyTo(combined.AsSpan(first.Length));
        return combined;
    }

    /// <summary>Decodes filtered bytes for the console writer.</summary>
    public static string Decode(byte[] data) => Encoding.UTF8.GetString(data);
}
