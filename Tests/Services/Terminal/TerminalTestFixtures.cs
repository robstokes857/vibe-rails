using System.Buffers.Binary;

namespace Tests.Services.Terminal;

/// <summary>
/// Shared infrastructure for the WaitingForUserInputObserver regression tests.
/// Pulled out so the four Session_*_WaitingObserverRegressionTests files (plus
/// the unit-level WaitingForUserInputObserverTests) don't each carry private
/// copies of <see cref="ManualTimeProvider"/>, <see cref="TimedChunk"/>, and a
/// hand-rolled fixture-binary loader.
/// </summary>
internal static class TerminalTestFixtures
{
    /// <summary>
    /// Binary fixture format: little-endian u32 chunk count, followed by N
    /// records each shaped (u32 byteCount, u32 msOffset, byteCount bytes).
    /// Produced by python-scripts/export_chunks_fixture.py. Bounded against
    /// truncation and bogus length prefixes so a corrupt fixture surfaces a
    /// clear error rather than an OOM or IndexOutOfRangeException.
    /// </summary>
    public static List<TimedChunk> LoadFixture(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 4)
            throw new InvalidDataException($"Fixture {path} is shorter than 4 bytes — no chunk count header.");

        var pos = 0;
        var count = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(pos, 4));
        pos += 4;

        // Cheap upper bound: each chunk header is 8 bytes plus its payload,
        // so `count` chunks can't possibly fit in the remaining bytes if it
        // exceeds (bytes.Length - 4) / 8. Catches obvious corruption (e.g.
        // count = uint.MaxValue) before we try to allocate a List of that
        // capacity.
        var maxPossibleChunks = (bytes.Length - 4) / 8;
        if (count > maxPossibleChunks)
            throw new InvalidDataException(
                $"Fixture {path} declares {count} chunks but only has bytes for at most {maxPossibleChunks}.");

        var chunks = new List<TimedChunk>((int)count);
        for (var i = 0u; i < count; i++)
        {
            if (pos + 8 > bytes.Length)
                throw new InvalidDataException($"Fixture {path} truncated at chunk {i} header (offset {pos}).");

            var byteCount = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(pos, 4));
            pos += 4;
            var msOffset = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(pos, 4));
            pos += 4;

            if (byteCount > (uint)(bytes.Length - pos))
                throw new InvalidDataException(
                    $"Fixture {path} chunk {i} declares {byteCount} bytes but only {bytes.Length - pos} remain.");

            var data = new byte[byteCount];
            Array.Copy(bytes, pos, data, 0, byteCount);
            pos += (int)byteCount;
            chunks.Add(new TimedChunk(data, msOffset));
        }
        return chunks;
    }
}

internal readonly record struct TimedChunk(byte[] Bytes, uint MsOffset);

/// <summary>
/// Hand-controllable <see cref="TimeProvider"/> shared by the observer tests.
/// Two interaction styles, both supported:
///   - Replay-style: ctor with a start instant, then <see cref="SetUtcNow"/>
///     to jump the clock to each chunk's timestamp.
///   - Step-style:  default ctor, then <see cref="Advance"/> to walk forward
///     in fixed increments.
/// </summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _utcNow;

    public ManualTimeProvider()
        : this(new DateTimeOffset(2026, 4, 26, 12, 0, 0, TimeSpan.Zero))
    {
    }

    public ManualTimeProvider(DateTimeOffset start)
    {
        _utcNow = start;
    }

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public void SetUtcNow(DateTimeOffset value) => _utcNow = value;

    public void Advance(TimeSpan value) => _utcNow += value;
}
