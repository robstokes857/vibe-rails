using System.Globalization;

namespace VibeRails.Services.BertV2;

internal static class BertDocumentId
{
    public static string Create(string sessionId, long userInputId)
    {
        return $"{sessionId}:{userInputId}";
    }

    public static BertParsedDocumentId Parse(string documentId)
    {
       ArgumentNullException.ThrowIfNullOrWhiteSpace(documentId);

        var separatorIndex = documentId.LastIndexOf(':');
        if (separatorIndex <= 0 || separatorIndex >= documentId.Length - 1)
            return new BertParsedDocumentId(documentId, null);

        var sessionId = documentId[..separatorIndex];
        if (long.TryParse(documentId[(separatorIndex + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var userInputId))
            return new BertParsedDocumentId(sessionId, userInputId);

        return new BertParsedDocumentId(sessionId, null);
    }
}
