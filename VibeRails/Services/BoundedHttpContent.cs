using System.Text.Json;

namespace VibeRails.Services;

/// <summary>Reads JSON only after enforcing a byte limit, including responses without a length.</summary>
internal static class BoundedHttpContent
{
    internal static async Task<JsonDocument> ReadJsonAsync(
        HttpContent content, int maxBytes, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > maxBytes)
            throw new InvalidDataException("HTTP response exceeds its byte limit.");

        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var body = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            // Read at most one byte beyond the cap to distinguish an exact fit from overflow.
            var count = await stream.ReadAsync(
                buffer.AsMemory(0, Math.Min(buffer.Length, maxBytes + 1 - (int)body.Length)), cancellationToken);
            if (count == 0)
                return JsonDocument.Parse(body.ToArray());
            body.Write(buffer, 0, count);
            if (body.Length > maxBytes)
                throw new InvalidDataException("HTTP response exceeds its byte limit.");
        }
    }
}
