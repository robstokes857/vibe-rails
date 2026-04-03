using System.Threading.Channels;
using Serilog;
using VibeRails.DB;

namespace VibeRails.Services.Terminal;

public sealed class SessionOutputWriter : ISessionOutputWriter
{
    private const int FlushThreshold = 5 * 1024 * 1024; // 5MB

    // Max bytes a partial CSI private-mode sequence can span (e.g. ESC[?1049;2004;25
    // without the trailing h/l). Multi-mode sequences can be longer than standalone ones.
    private const int MaxResidualLength = 30;

    private readonly IRepository _repository;
    private readonly Channel<WriterMessage> _channel = Channel.CreateUnbounded<WriterMessage>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private Task? _worker;
    private string? _sessionId;
    private int _disposed;

    // Drain-loop state (single-reader, no locking needed)
    private MemoryStream _mainBuffer = new();
    private MemoryStream _altBuffer = new();
    private bool _inAltScreen;
    private int _cols = 120;
    private int _rows = 30;
    private int _sequence;

    // Holds trailing bytes from the previous payload that could be the start of an
    // incomplete alt-screen escape sequence split across PTY reads.
    private byte[]? _residual;

    public SessionOutputWriter(IRepository repository)
    {
        _repository = repository;
    }

    public void Initialize(string sessionId, int cols = 120, int rows = 30)
    {
        _sessionId = sessionId;
        _cols = cols;
        _rows = rows;
        _worker = Task.Run(DrainAsync);
    }

    public void Enqueue(byte[] payload)
    {
        if (payload.Length == 0 || Volatile.Read(ref _disposed) == 1)
            return;

        _channel.Writer.TryWrite(new WriterMessage(WriterMessageKind.Data, payload));
    }

    public void NotifyResize(int cols, int rows)
    {
        if (Volatile.Read(ref _disposed) == 1)
            return;

        _channel.Writer.TryWrite(new WriterMessage(WriterMessageKind.Resize, null, cols, rows));
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        _channel.Writer.TryComplete();

        if (_worker != null)
        {
            try
            {
                await _worker.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[SessionOutputWriter] Shutdown failed for session {SessionId}", _sessionId);
            }
        }

        _mainBuffer.Dispose();
        _altBuffer.Dispose();
    }

    private async Task DrainAsync()
    {
        await foreach (var msg in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                switch (msg.Kind)
                {
                    case WriterMessageKind.Data:
                        await HandleDataAsync(msg.Payload!).ConfigureAwait(false);
                        break;
                    case WriterMessageKind.Resize:
                        await HandleResizeAsync(msg.NewCols, msg.NewRows).ConfigureAwait(false);
                        break;
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[SessionOutputWriter] Drain error for session {SessionId}", _sessionId);
            }
        }

        // Session ended — flush residual into active buffer, then flush both buffers
        if (_residual is { Length: > 0 })
        {
            ActiveBuffer.Write(_residual, 0, _residual.Length);
            _residual = null;
        }
        await FlushBufferAsync(_mainBuffer, false).ConfigureAwait(false);
        await FlushBufferAsync(_altBuffer, true).ConfigureAwait(false);
    }

    private async Task HandleDataAsync(byte[] payload)
    {
        // Write to legacy SessionLogs (unchanged, per-chunk — always the original payload)
        try
        {
            await _repository.LogSessionOutputAsync(_sessionId!, payload, false).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[SessionOutputWriter] Failed to persist legacy output for session {SessionId}", _sessionId);
        }

        // Build working data: prepend any residual from the previous call so that
        // alt-screen sequences split across PTY reads are detected correctly.
        byte[] workData;
        if (_residual is { Length: > 0 })
        {
            workData = new byte[_residual.Length + payload.Length];
            _residual.CopyTo(workData, 0);
            payload.CopyTo(workData, _residual.Length);
            _residual = null;
        }
        else
        {
            workData = payload;
        }

        // Process data for the buffered enriched table — split at alt screen transitions.
        // Key invariant: flush the *current* buffer BEFORE switching screens so that
        // sequence numbers reflect chronological order, and each chunk contains data
        // for exactly one screen mode.
        var offset = 0;
        while (offset < workData.Length)
        {
            var transition = FindNextAltScreenTransition(workData, offset);

            if (transition == null)
            {
                // No more transitions found — write remaining data to active buffer,
                // but hold back any trailing bytes that could be an incomplete sequence
                // split across the next PTY read.
                var tailResidual = GetTailResidualLength(workData, offset);
                var writeEnd = workData.Length - tailResidual;
                if (writeEnd > offset)
                    ActiveBuffer.Write(workData, offset, writeEnd - offset);
                if (tailResidual > 0)
                    _residual = workData[writeEnd..];
                offset = workData.Length;
            }
            else
            {
                var (transitionOffset, transitionLength, enteringAlt) = transition.Value;
                var endOfChunk = transitionOffset + transitionLength;

                if (enteringAlt)
                {
                    // Write data BEFORE the enter sequence to the main buffer
                    if (transitionOffset > offset)
                        _mainBuffer.Write(workData, offset, transitionOffset - offset);

                    // Flush main buffer so it gets a sequence number BEFORE the alt content
                    await FlushBufferAsync(_mainBuffer, false).ConfigureAwait(false);

                    // The enter sequence goes to the alt buffer so replay
                    // properly enters alt-screen from the alt chunk
                    _altBuffer.Write(workData, transitionOffset, transitionLength);
                    _inAltScreen = true;
                }
                else
                {
                    // Write data up to AND including the exit sequence to the alt buffer
                    if (endOfChunk > offset)
                        _altBuffer.Write(workData, offset, endOfChunk - offset);

                    // Flush alt buffer — it now contains enter seq + alt content + exit seq
                    await FlushBufferAsync(_altBuffer, true).ConfigureAwait(false);
                    _inAltScreen = false;
                }

                offset = endOfChunk;
            }
        }

        // Check if active buffer exceeded threshold
        if (ActiveBuffer.Length >= FlushThreshold)
        {
            await FlushBufferAsync(ActiveBuffer, _inAltScreen).ConfigureAwait(false);
        }
    }

    private async Task HandleResizeAsync(int newCols, int newRows)
    {
        // Flush both buffers at old dimensions
        await FlushBufferAsync(_mainBuffer, false).ConfigureAwait(false);
        await FlushBufferAsync(_altBuffer, true).ConfigureAwait(false);

        _cols = newCols;
        _rows = newRows;
    }

    private async Task FlushBufferAsync(MemoryStream buffer, bool isAlternateScreen)
    {
        if (buffer.Length == 0)
            return;

        var data = buffer.ToArray();
        buffer.SetLength(0);

        try
        {
            await _repository.InsertTerminalSessionLogAsync(
                _sessionId!, _sequence++, data, isAlternateScreen, _cols, _rows
            ).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[SessionOutputWriter] Failed to persist enriched output for session {SessionId}", _sessionId);
        }
    }

    private MemoryStream ActiveBuffer => _inAltScreen ? _altBuffer : _mainBuffer;

    /// <summary>
    /// Finds the next alternate screen transition in the data starting from offset.
    /// Returns (offset of ESC, length of full sequence, true=entering alt / false=exiting alt), or null.
    /// Handles both standalone (ESC[?1049h) and multi-mode (ESC[?1049;25h) sequences.
    /// Recognised alt-screen modes: 47, 1047, 1049.
    /// </summary>
    private static (int Offset, int Length, bool EnteringAlt)? FindNextAltScreenTransition(byte[] data, int start)
    {
        for (var i = start; i < data.Length - 2; i++)
        {
            // Look for ESC [ ?
            if (data[i] != 0x1B || data[i + 1] != 0x5B)
                continue;
            if (i + 2 >= data.Length || data[i + 2] != 0x3F) // '?'
                continue;

            // Parse the CSI parameter list: semicolon-separated decimal numbers
            // ending with a final byte in the 0x40-0x7E range (we care about 'h'/'l').
            var paramStart = i + 3;
            var pos = paramStart;
            var hasAltMode = false;
            var currentParam = 0;
            var hadDigit = false;

            while (pos < data.Length)
            {
                var b = data[pos];
                if (b >= 0x30 && b <= 0x39) // digit
                {
                    currentParam = currentParam * 10 + (b - 0x30);
                    hadDigit = true;
                    pos++;
                }
                else if (b == 0x3B) // ';' — parameter separator
                {
                    if (hadDigit && (currentParam == 47 || currentParam == 1047 || currentParam == 1049))
                        hasAltMode = true;
                    currentParam = 0;
                    hadDigit = false;
                    pos++;
                }
                else if (b >= 0x40 && b <= 0x7E) // final byte
                {
                    // Check the last accumulated parameter
                    if (hadDigit && (currentParam == 47 || currentParam == 1047 || currentParam == 1049))
                        hasAltMode = true;

                    if (hasAltMode && (b == 0x68 || b == 0x6C)) // 'h' or 'l'
                    {
                        var seqLen = pos - i + 1;
                        return (i, seqLen, b == 0x68);
                    }

                    // Valid CSI but no alt-screen mode — stop parsing this sequence
                    break;
                }
                else
                {
                    // Unexpected byte — malformed sequence, stop
                    break;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Checks if the tail of data[offset..] could be the start of an incomplete
    /// CSI private-mode sequence that might contain an alt-screen mode (47/1047/1049).
    /// Returns how many trailing bytes to hold back.
    /// </summary>
    private static int GetTailResidualLength(byte[] data, int offset)
    {
        // Look backwards from the end for an ESC byte (0x1B) within the last MaxResidualLength bytes.
        var searchStart = Math.Max(offset, data.Length - MaxResidualLength);
        for (var escPos = data.Length - 1; escPos >= searchStart; escPos--)
        {
            if (data[escPos] != 0x1B) continue;

            var remaining = data.Length - escPos;

            // Single ESC at the very end — could be start of anything
            if (remaining == 1) return 1;

            // ESC not followed by [ — not our sequence, skip this ESC
            if (data[escPos + 1] != 0x5B) continue;

            // ESC[ — could be our sequence, check prefix validity
            if (remaining == 2) return 2;

            // ESC[? — required prefix for private-mode sequences
            if (data[escPos + 2] != 0x3F) continue;
            if (remaining == 3) return 3;

            // After ESC[?, check if the remaining bytes are valid CSI params
            // (digits and semicolons) without a final byte. If so, this is an
            // incomplete private-mode sequence — hold it back.
            var allParamChars = true;
            for (var k = escPos + 3; k < data.Length; k++)
            {
                var b = data[k];
                if ((b >= 0x30 && b <= 0x39) || b == 0x3B) // digit or ';'
                    continue;
                if (b >= 0x40 && b <= 0x7E) // final byte — sequence is complete, no residual
                {
                    allParamChars = false;
                    break;
                }
                // Unexpected byte — not a valid CSI sequence
                allParamChars = false;
                break;
            }

            if (allParamChars)
                return remaining; // incomplete CSI ?... sequence, hold it back

            continue; // complete or invalid — try earlier ESC
        }

        return 0;
    }

    private readonly record struct WriterMessage(
        WriterMessageKind Kind,
        byte[]? Payload,
        int NewCols = 0,
        int NewRows = 0);

    private enum WriterMessageKind { Data, Resize }
}
