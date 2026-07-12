namespace TokenSaver.Minify;

/// <summary>
/// Read-only stream that replays an already-buffered prefix, then continues from the live request
/// body. Used on the cap-overflow fail-open path: by the time we know a chunked body is too large
/// to rewrite, the prefix has been consumed off the wire — this stitches it back in front of the
/// remainder so the upstream still receives every byte.
/// </summary>
internal sealed class PrefixedBodyStream(ReadOnlyMemory<byte> prefix, Stream rest) : Stream
{
    private int _prefixRead;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var fromPrefix = ReadFromPrefix(buffer);
        return fromPrefix > 0 ? fromPrefix : rest.Read(buffer);
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var fromPrefix = ReadFromPrefix(buffer.Span);
        return fromPrefix > 0 ? fromPrefix : await rest.ReadAsync(buffer, cancellationToken);
    }

    public override Task<int> ReadAsync(
        byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    private int ReadFromPrefix(Span<byte> buffer)
    {
        var remaining = prefix.Length - _prefixRead;
        if (remaining <= 0)
            return 0;

        var count = Math.Min(remaining, buffer.Length);
        prefix.Span.Slice(_prefixRead, count).CopyTo(buffer);
        _prefixRead += count;
        return count;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
