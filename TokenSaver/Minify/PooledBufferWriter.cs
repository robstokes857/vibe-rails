using System.Buffers;

namespace TokenSaver.Minify;

/// <summary>
/// Minimal <see cref="IBufferWriter{T}"/> over an <see cref="ArrayPool{T}"/> array, so a request
/// rewrite never allocates its output buffer on the GC heap (unlike <c>ArrayBufferWriter</c>).
/// Dispose returns the array; the written memory is invalid afterwards.
/// </summary>
internal sealed class PooledBufferWriter(int initialCapacity) : IBufferWriter<byte>, IDisposable
{
    private byte[] _buffer = ArrayPool<byte>.Shared.Rent(Math.Max(initialCapacity, 256));
    private int _written;

    public ReadOnlyMemory<byte> WrittenMemory => _buffer.AsMemory(0, _written);

    public int WrittenCount => _written;

    /// <summary>Rewinds to empty for reuse; the underlying rented array is kept.</summary>
    public void Clear() => _written = 0;

    public void Advance(int count) => _written += count;

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return _buffer.AsMemory(_written);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return _buffer.AsSpan(_written);
    }

    private void EnsureCapacity(int sizeHint)
    {
        if (sizeHint < 1)
            sizeHint = 1;
        if (_buffer.Length - _written >= sizeHint)
            return;

        var grown = ArrayPool<byte>.Shared.Rent(Math.Max(_written + sizeHint, _buffer.Length * 2));
        _buffer.AsSpan(0, _written).CopyTo(grown);
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = grown;
    }

    public void Dispose()
    {
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = [];
        _written = 0;
    }
}
