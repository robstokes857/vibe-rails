using System.Text.Json;

namespace VibeRails.Services.Board;

/// <summary>A durable outbound event. Public replay URLs remain local to this ledger.</summary>
public sealed record BoardJiraDelivery(string Id, string CardId, string ProjectPath, string ConnectionId,
    string IssueId, string Kind, string SourceId, string Body, string Status, string? Message, string? Url);

/// <summary>Per-comment Jira policy, separate from hosted Board synchronization.</summary>
public static class BoardJiraCommentPolicy
{
    public static string? Changes(BoardAuthor author, bool syncToJira)
    {
        var purpose = BoardCommentPurpose.Changes(author);
        return syncToJira ? purpose : purpose is null ? "{\"syncToJira\":{\"to\":false}}"
            : purpose[..^1] + ",\"syncToJira\":{\"to\":false}}";
    }

    public static bool ShouldSync(string? changes)
    {
        if (string.IsNullOrWhiteSpace(changes)) return true;
        try
        {
            using var json = JsonDocument.Parse(changes);
            return !json.RootElement.TryGetProperty("syncToJira", out var field)
                || field.ValueKind != JsonValueKind.Object || !field.TryGetProperty("to", out var value)
                || value.ValueKind != JsonValueKind.False;
        }
        catch (JsonException) { return true; }
    }
}

public partial interface IBoardStore
{
    /// <summary>Reads bounded pending events; callers serialize delivery with the Jira lock.</summary>
    Task<IReadOnlyList<BoardJiraDelivery>> GetPendingJiraDeliveriesAsync(CancellationToken cancellationToken = default);
    /// <summary>Reads local delivery status for a live card in its owning project.</summary>
    Task<IReadOnlyList<BoardJiraDelivery>> GetJiraDeliveriesAsync(string projectPath, string cardId, CancellationToken cancellationToken = default);
    /// <summary>Compare-and-set transition; a crash during sending must never cause a blind repost.</summary>
    Task<bool> SetJiraDeliveryAsync(string id, string expectedStatus, string status, string? message, string? url, CancellationToken cancellationToken = default);
    /// <summary>After acquiring the cross-process lock, surface interrupted writes without retrying them.</summary>
    Task RecoverJiraDeliveriesAsync(CancellationToken cancellationToken = default);
}
