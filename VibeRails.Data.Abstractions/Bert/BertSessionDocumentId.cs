using System.Globalization;

namespace VibeRails.Services.BertV2;

internal static class BertSessionDocumentId
{
    // Prefix keeps these ids unambiguous against the per-message keys
    // ("<sessionId>:<userInputId>") if anything ever queries across both stores.
    public const string Prefix = "sess:";

    public static string Create(string sessionId, int chunkIndex)
    {
        return $"{Prefix}{sessionId}:{chunkIndex}";
    }

    public static BertParsedSessionDocumentId? Parse(string? documentId)
    {
        if (string.IsNullOrWhiteSpace(documentId))
            return null;
        if (!documentId.StartsWith(Prefix, StringComparison.Ordinal))
            return null;

        var body = documentId[Prefix.Length..];
        var separatorIndex = body.LastIndexOf(':');
        if (separatorIndex <= 0 || separatorIndex >= body.Length - 1)
            return null;

        var sessionId = body[..separatorIndex];
        if (!int.TryParse(body[(separatorIndex + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var chunkIndex))
            return null;

        return new BertParsedSessionDocumentId(sessionId, chunkIndex);
    }
}

public sealed record BertParsedSessionDocumentId(string SessionId, int ChunkIndex);
