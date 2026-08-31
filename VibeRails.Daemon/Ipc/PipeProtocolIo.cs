using System.Text;

namespace VibeRails.Daemon.Ipc;

internal static class PipeProtocolIo
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static async Task<string?> ReadLineAsync(
        Stream stream,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (maximumBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));

        var buffer = new byte[Math.Min(1024, maximumBytes + 1)];
        using var collected = new MemoryStream(Math.Min(maximumBytes, 4096));

        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                return collected.Length == 0 ? null : Decode(collected);

            var newline = Array.IndexOf(buffer, (byte)'\n', 0, read);
            var count = newline >= 0 ? newline : read;
            if (collected.Length + count > maximumBytes)
                throw new InvalidDataException($"IPC message exceeds the {maximumBytes}-byte limit.");

            collected.Write(buffer, 0, count);
            if (newline >= 0)
                return Decode(collected);
        }
    }

    public static async Task WriteLineAsync(
        Stream stream,
        string value,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(value);
        if (maximumBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));

        var bytes = StrictUtf8.GetBytes(value);
        if (bytes.Length > maximumBytes)
            throw new InvalidDataException($"IPC message exceeds the {maximumBytes}-byte limit.");

        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync("\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string Decode(MemoryStream collected)
    {
        var bytes = collected.GetBuffer();
        var length = checked((int)collected.Length);
        if (length > 0 && bytes[length - 1] == (byte)'\r')
            length--;
        return StrictUtf8.GetString(bytes, 0, length);
    }
}
