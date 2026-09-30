namespace VibeRails.Services.Board;

/// <summary>Portable hosted board identity; no paths, credentials or local launch settings.</summary>
public sealed record BoardRemoteDescriptor(string RemoteBoardId, string Name, string KeyPrefix, string? DisplayPrefix,
    IReadOnlyList<BoardRemoteLane> Lanes, bool IsOwner, long LastSeq);
/// <summary>A portable lane, excluding local Automation bindings.</summary>
public sealed record BoardRemoteLane(string Id, string Name, string? Color, int Position);
/// <summary>Owner-visible invitation state; declines and blocks stay private.</summary>
public sealed record BoardSharingInvite(string Id, string Email, string Status);
/// <summary>The owner-visible collaborator list and slot limit.</summary>
public sealed record BoardSharingOverview(int Limit, IReadOnlyList<BoardSharingInvite> Invitations);
/// <summary>An invitation address supplied to the hosted owner API.</summary>
public sealed record BoardSharingEmailRequest(string? Email);
/// <summary>A generic acknowledgment without recipient account information.</summary>
public sealed record BoardSharingResult(string? Message = null, bool Saved = true);
/// <summary>The local board identity and any initial synchronization error.</summary>
public sealed record BoardSharedImportResult(string BoardId, string Name, string? SyncError = null);
/// <summary>The configured account permission and optional owner controls.</summary>
public sealed record BoardSharingState(bool Configured, bool IsOwner, string? RemoteBoardId, BoardSharingOverview? Sharing);

public partial interface IBoardStore
{
    /// <summary>Atomically imports an accepted board once, binding it permanently to this destination.</summary>
    Task<BoardRecord> ImportSharedBoardAsync(string projectPath, BoardRemoteDescriptor remote, string destinationKey,
        CancellationToken cancellationToken = default);
    /// <summary>Applies portable layout only if the local layout still matches the caller's snapshot.</summary>
    Task<string?> ApplyRemoteLayoutAsync(string projectPath, string boardId, BoardRemoteDescriptor remote,
        BoardRecord expectedBoard, IReadOnlyList<BoardColumnRecord> expectedColumns, CancellationToken cancellationToken = default);
    /// <summary>Replays a transfer's restore event on an imported board without echoing it.</summary>
    Task<bool> RestoreSharedCardAsync(string projectPath, string cardId, BoardAuthor author, BoardSyncStamp stamp,
        CancellationToken cancellationToken = default);
}

/// <summary>Shared layout fingerprint used by transactional persistence and the transport.</summary>
public static class BoardLayoutHash
{
    /// <summary>Hashes the portable layout in canonical lane order.</summary>
    public static string Compute(string name, string prefix, string? displayPrefix, IEnumerable<BoardRemoteLane> lanes)
    {
        var canonical = new System.Text.StringBuilder().Append(name).Append('\n').Append(prefix).Append('\n').Append(displayPrefix);
        var index = 0;
        foreach (var lane in lanes.OrderBy(l => l.Position))
            canonical.Append('\n').Append(lane.Id).Append('\t').Append(lane.Name).Append('\t')
                .Append(string.IsNullOrWhiteSpace(lane.Color) ? null : lane.Color).Append('\t').Append(index++);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical.ToString())));
    }
}
