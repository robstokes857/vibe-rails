using System.Text;

namespace VibeRails.Services.Board;

/// <summary>Immutable bytes and their card-scoped, untrusted display metadata.</summary>

public partial interface IBoardService
{
    Task<BoardAttachmentContent?> GetAttachmentContentAsync(string projectPath, string idOrKey, string attachmentId, CancellationToken cancellationToken = default);
}

public sealed partial class BoardService
{
    // Attachments have no size limit, per-file or per-card. Only the count is bounded, because
    // that is a list people have to scan, not a number of bytes. The board holds one local user's
    // own files on their own disk; a file too big to be practical is their problem, not a rule.
    public const int MaxAttachmentTextCharacters = 100_000;

    public Task<BoardAttachmentContent?> GetAttachmentContentAsync(string projectPath, string idOrKey, string attachmentId, CancellationToken cancellationToken = default) =>
        store.GetAttachmentContentAsync(projectPath, idOrKey, attachmentId, cancellationToken);

    internal static byte[] DecodeAttachmentDataUrl(string? dataUrl) => BoardAttachmentData.DecodeDataUrl(dataUrl);

    internal static string NormalizeAttachmentName(string? value)
    {
        // This is a label, never a filesystem path. Strip either platform's separators and
        // controls also used for header injection and misleading bidi filename displays.
        var name = (value ?? "attachment").Replace('\\', '/').Split('/').Last();
        name = string.Concat(name.Where(c => !char.IsControl(c) && c is not '\u202a' and not '\u202b'
            and not '\u202c' and not '\u202d' and not '\u202e' and not '\u2066' and not '\u2067'
            and not '\u2068' and not '\u2069')).Trim();
        if (name is "" or "." or "..") name = "attachment";
        if (name.Length > 180) name = name[..180];
        return name;
    }

    internal static string DetectAttachmentMimeType(string name, ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return "image/png";
        if (bytes.StartsWith(new byte[] { 255, 216, 255 })) return "image/jpeg";
        if (bytes.StartsWith("GIF87a"u8) || bytes.StartsWith("GIF89a"u8)) return "image/gif";
        if (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("WEBP"u8)) return "image/webp";
        var extension = Path.GetExtension(name).ToLowerInvariant();
        if (extension is ".md" or ".markdown") return "text/markdown";
        if (extension == ".txt") return "text/plain";
        if (extension == ".pdf" && bytes.StartsWith("%PDF-"u8)) return "application/pdf";
        return "application/octet-stream";
    }

    public static string ReadAttachmentText(BoardAttachmentContent attachment, int offset = 0, int maxCharacters = 20_000)
    {
        if (attachment.Attachment.MimeType is not ("text/plain" or "text/markdown"))
            throw new BoardValidationException("Only Markdown and TXT attachments can be read as text. Download other file types from the card.");
        if (offset < 0 || maxCharacters is < 1 or > MaxAttachmentTextCharacters)
            throw new BoardValidationException($"Offset must be nonnegative and maxCharacters must be 1–{MaxAttachmentTextCharacters}.");
        string text;
        try { text = new UTF8Encoding(false, true).GetString(attachment.Content).TrimStart('\uFEFF'); }
        catch (DecoderFallbackException) { throw new BoardValidationException("This text file is not valid UTF-8. Download it to open with another encoding."); }
        if (offset >= text.Length) return string.Empty;
        var length = Math.Min(maxCharacters, text.Length - offset);
        return text.Substring(offset, length);
    }
}
