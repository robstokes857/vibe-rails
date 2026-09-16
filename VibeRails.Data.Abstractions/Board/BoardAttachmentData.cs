using VibeRails.DTOs;
namespace VibeRails.Services.Board;
public sealed record BoardAttachmentContent(BoardAttachmentRecord Attachment, byte[] Content);
internal static class BoardAttachmentData
{
    internal const int MaxAttachmentsPerCard = 12;
    internal static byte[] DecodeDataUrl(string? dataUrl)
    {
        if (dataUrl is null)
            throw new BoardValidationException("The attachment must contain base64 file content.");
        var separator = dataUrl.IndexOf(',');
        if (separator is < 12 or > 255 || !dataUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            || !dataUrl.AsSpan(0, separator).EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
            throw new BoardValidationException("The attachment must contain base64 file content.");
        var encoded = dataUrl.AsSpan(separator + 1);
        // Reject whitespace and alternate alphabets before allocating decoded bytes.
        foreach (var character in encoded)
            if (!(character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '+' or '/' or '='))
                throw new BoardValidationException("The attachment has invalid base64 content.");
        try { return Convert.FromBase64String(dataUrl[(separator + 1)..]); }
        catch (FormatException) { throw new BoardValidationException("The attachment has invalid base64 content."); }
    }

    internal static BoardAttachmentDto ToDto(BoardAttachmentRecord attachment) =>
        new(attachment.Id, attachment.Name, attachment.DataUrl, attachment.MimeType, attachment.Bytes, attachment.CreatedUtc);
}
