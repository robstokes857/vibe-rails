using System.Text.Json;
using VibeRails.DTOs;

namespace VibeRails.Services.Board;

/// <summary>Portable purpose snapshot for new agent comments; no historical reclassification.</summary>
public static class BoardCommentPurpose
{
    public const string ChangeField = "agentPurpose";

    /// <summary>Only known enum values can be written; no arbitrary text enters this JSON.</summary>
    public static string? Changes(BoardAuthor author) => author.Kind == BoardAuthor.AgentKind && AgentPurpose.IsValid(author.Purpose)
        ? "{\"agentPurpose\":{\"to\":\"" + author.Purpose + "\"}}" : null;

    /// <summary>Absent or malformed metadata leaves a comment unclassified.</summary>
    public static string? Read(string? changes)
    {
        if (string.IsNullOrEmpty(changes)) return null;
        try
        {
            using var document = JsonDocument.Parse(changes);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty(ChangeField, out var field) && field.ValueKind == JsonValueKind.Object
                && field.TryGetProperty("to", out var value) && value.ValueKind == JsonValueKind.String
                && AgentPurpose.IsValid(value.GetString())) return value.GetString();
        }
        catch (JsonException) { }
        return null;
    }
}
