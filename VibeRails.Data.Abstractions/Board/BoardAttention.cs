using System.Text.Json;

namespace VibeRails.Services.Board;

/// <summary>Validation and portable discussion metadata for an agent's attention request.</summary>
public static class BoardAttention
{
    public const string ChangeField = "attention";
    public const string CommentChanges = "{\"attention\":{\"to\":true}}";

    /// <summary>Validate before any part of a flag update is saved.</summary>
    public static string? NormalizeReason(bool? flagged, string? reason, BoardAuthor? author)
    {
        if (reason is not null && flagged != true)
            throw new BoardValidationException("flagReason can only be used with flagged=true.");
        if (flagged == true && (author?.Kind == BoardAuthor.AgentKind || reason is not null))
        {
            if (string.IsNullOrWhiteSpace(reason))
                throw new BoardValidationException("flagged=true requires flagReason explaining the major issue and what the user needs to do. It will be saved as an attention comment.");
            reason = reason.Trim();
            if (reason.Length > 50_000)
                throw new BoardValidationException("Flag reason is too long (max 50000 characters).");
        }
        return reason;
    }

    /// <summary>Old comments and unrelated or malformed metadata are ordinary discussion.</summary>
    public static bool IsAttention(string? changes)
    {
        if (string.IsNullOrEmpty(changes)) return false;
        try
        {
            using var document = JsonDocument.Parse(changes);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty(ChangeField, out var field)
                && field.ValueKind == JsonValueKind.Object
                && field.TryGetProperty("to", out var value) && value.ValueKind == JsonValueKind.True;
        }
        catch (JsonException) { return false; }
    }
}
