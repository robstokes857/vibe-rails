using System.Threading.Channels;
using Serilog;
using VibeRails.DB;

namespace VibeRails.Services.Terminal;

public sealed class SessionOutputWriter : ISessionOutputWriter
{
    private const int FlushThreshold = 5 * 1024 * 1024; // 5MB

    // Longest alt-screen sequence is ESC[?1049h = 8 bytes; an incomplete prefix is at most 7.
    private const int MaxResidualLength = 7;

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

                    // The enter sequence (ESC[?1049h) goes to the alt buffer so replay
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
    /// Sequences: ESC[?1049h/l (8 bytes), ESC[?47h/l (6 bytes)
    /// </summary>
    private static (int Offset, int Length, bool EnteringAlt)? FindNextAltScreenTransition(byte[] data, int start)
    {
        for (var i = start; i < data.Length - 2; i++)
        {
            if (data[i] != 0x1B || data[i + 1] != 0x5B)
                continue;

            // ESC[?1049h / ESC[?1049l  (8 bytes total)
            if (i + 7 < data.Length &&
                data[i + 2] == 0x3F && // ?
                data[i + 3] == 0x31 && // 1
                data[i + 4] == 0x30 && // 0
                data[i + 5] == 0x34 && // 4
                data[i + 6] == 0x39)   // 9
            {
                if (data[i + 7] == 0x68) // h — enter
                    return (i, 8, true);
                if (data[i + 7] == 0x6C) // l — exit
                    return (i, 8, false);
            }

            // ESC[?47h / ESC[?47l  (6 bytes total)
            if (i + 5 < data.Length &&
                data[i + 2] == 0x3F && // ?
                data[i + 3] == 0x34 && // 4
                data[i + 4] == 0x37)   // 7
            {
                if (data[i + 5] == 0x68) // h — enter
                    return (i, 6, true);
                if (data[i + 5] == 0x6C) // l — exit
                    return (i, 6, false);
            }
        }

        return null;
    }

    /// <summary>
    /// Checks if the tail of data[offset..] could be the start of an incomplete
    /// alt-screen escape sequence. Returns how many trailing bytes to hold back.
    /// </summary>
    private static int GetTailResidualLength(byte[] data, int offset)
    {
        // Look backwards from the end for an ESC byte (0x1B) within the last MaxResidualLength bytes.
        // If found, check whether the bytes after it are a valid prefix of our target sequences.
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

            // ESC[? — required prefix for our sequences
            if (data[escPos + 2] != 0x3F) continue;
            if (remaining == 3) return 3;

            // ESC[?1... or ESC[?4... — valid prefixes for ?1049 or ?47
            var d3 = data[escPos + 3];
            if (d3 == 0x31) // '1' — prefix of ?1049
            {
                // ESC[?1, ESC[?10, ESC[?104, ESC[?1049 (need h/l)
                if (remaining <= 7) // up to ESC[?1049 (7 bytes, missing final h/l)
                {
                    // Validate each byte in the prefix: 0, 4, 9
                    ReadOnlySpan<byte> expected = [0x30, 0x34, 0x39]; // '0', '4', '9'
                    for (var k = 0; k < remaining - 4 && k < expected.Length; k++)
                    {
                        if (data[escPos + 4 + k] != expected[k])
                            goto nextEsc; // not a valid ?1049 prefix
                    }
                    return remaining;
                }
            }
            else if (d3 == 0x34) // '4' — prefix of ?47
            {
                // ESC[?4, ESC[?47 (need h/l)
                if (remaining == 4) return 4;
                if (remaining == 5 && data[escPos + 4] == 0x37) return 5; // ESC[?47
            }

            // Not a valid prefix of our target sequences
            nextEsc:;
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
