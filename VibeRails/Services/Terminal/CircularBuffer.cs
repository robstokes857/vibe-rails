namespace VibeRails.Services.Terminal;

/// <summary>
/// Circular buffer for storing the last N bytes of terminal output.
/// Thread-safe via lock-based synchronization.
/// Tracks ANSI "break points" — sequences that represent clean screen state
/// starts — so replay can begin from a known-good position.
/// </summary>
internal sealed class CircularBuffer
{
    // Break point sequences, in order of detection priority.
    // Each is a clean restart point for xterm replay.
    private static readonly byte[][] BreakPointSequences =
    [
        "\x1b[?1049h"u8.ToArray(),  // Enter alternate screen (Claude/Codex startup)
        "\x1b[2J"u8.ToArray(),      // Erase entire display
        "\x1bc"u8.ToArray(),        // Full terminal reset
    ];

    private readonly byte[] _buffer;
    private readonly int _capacity;
    private int _start;
    private int _count;
    private long _totalWritten;
    private readonly List<long> _breakPoints = [];
    private readonly Lock _lock = new();

    public CircularBuffer(int capacity)
    {
        _capacity = capacity;
        _buffer = new byte[capacity];
    }

    /// <summary>
    /// Append data to the buffer. If buffer is full, oldest data is overwritten.
    /// Scans for ANSI break point sequences and records their positions.
    /// </summary>
    public void Append(ReadOnlySpan<byte> data)
    {
        lock (_lock)
        {
            // Scan for break point sequences in the incoming data before writing,
            // so we can record their absolute positions.
            ScanForBreakPoints(data);

            for (int i = 0; i < data.Length; i++)
            {
                var writePos = (_start + _count) % _capacity;
                _buffer[writePos] = data[i];

                if (_count < _capacity)
                {
                    _count++;
                }
                else
                {
                    _start = (_start + 1) % _capacity;
                }
            }

            _totalWritten += data.Length;

            // Prune break points that have been overwritten (now behind buffer start)
            var oldestAbsolute = _totalWritten - _count;
            while (_breakPoints.Count > 0 && _breakPoints[0] < oldestAbsolute)
                _breakPoints.RemoveAt(0);
        }
    }

    /// <summary>
    /// Get a copy of all buffered data in order (oldest to newest).
    /// </summary>
    public byte[] GetData()
    {
        lock (_lock)
        {
            if (_count == 0) return [];

            var result = new byte[_count];
            for (int i = 0; i < _count; i++)
            {
                result[i] = _buffer[(_start + i) % _capacity];
            }
            return result;
        }
    }

    /// <summary>
    /// Returns buffered data starting from the last recorded ANSI break point.
    /// Falls back to GetData() if no break points have been recorded.
    /// This gives a clean replay start position without mid-sequence artifacts.
    /// </summary>
    public byte[] GetDataFromLastBreakPoint()
    {
        lock (_lock)
        {
            if (_count == 0) return [];

            if (_breakPoints.Count == 0)
                return GetDataLocked();

            var lastBreakPoint = _breakPoints[^1];
            var oldestAbsolute = _totalWritten - _count;

            // How many bytes from the start of the buffer to skip
            var skipBytes = (int)(lastBreakPoint - oldestAbsolute);
            if (skipBytes < 0) skipBytes = 0;
            if (skipBytes >= _count) return [];

            var length = _count - skipBytes;
            var result = new byte[length];
            for (int i = 0; i < length; i++)
            {
                result[i] = _buffer[(_start + skipBytes + i) % _capacity];
            }
            return result;
        }
    }

    /// <summary>
    /// Clear the buffer.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _start = 0;
            _count = 0;
            _totalWritten = 0;
            _breakPoints.Clear();
        }
    }

    private byte[] GetDataLocked()
    {
        if (_count == 0) return [];
        var result = new byte[_count];
        for (int i = 0; i < _count; i++)
            result[i] = _buffer[(_start + i) % _capacity];
        return result;
    }

    private void ScanForBreakPoints(ReadOnlySpan<byte> data)
    {
        foreach (var seq in BreakPointSequences)
        {
            var seqSpan = seq.AsSpan();
            int searchFrom = 0;
            while (searchFrom < data.Length)
            {
                var idx = data[searchFrom..].IndexOf(seqSpan);
                if (idx < 0) break;

                // Record the absolute position of this break point
                _breakPoints.Add(_totalWritten + searchFrom + idx);
                searchFrom += idx + seqSpan.Length;
            }
        }
    }
}
