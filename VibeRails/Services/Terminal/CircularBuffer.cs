namespace VibeRails.Services.Terminal;

/// <summary>
/// Circular buffer for storing the last N bytes of terminal output.
/// Thread-safe via lock-based synchronization.
/// </summary>
internal sealed class CircularBuffer
{
    private readonly byte[] _buffer;
    private readonly int _capacity;
    private int _start;
    private int _count;
    private readonly Lock _lock = new();

    public CircularBuffer(int capacity)
    {
        _capacity = capacity;
        _buffer = new byte[capacity];
    }

    /// <summary>
    /// Append data to the buffer. If buffer is full, oldest data is overwritten.
    /// </summary>
    public void Append(ReadOnlySpan<byte> data)
    {
        lock (_lock)
        {
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
    /// Clear the buffer.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _start = 0;
            _count = 0;
        }
    }
}
