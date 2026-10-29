namespace VibeRails.Services.Board.Sync;

/// <summary>
/// Owner invitation management. The hosted API checks ownership on every call. Collaborators read
/// shared boards on viberails.ai; the desktop no longer imports them (VIBE-235).
/// </summary>
public sealed class BoardSharingService(IBoardStore store, IBoardSyncClient client, IBoardSyncService sync)
{
    /// <summary>Loads hosted ownership before returning collaborator controls.</summary>
    public async Task<BoardSharingState> GetAsync(string project, string boardId, CancellationToken ct)
    {
        if (await store.GetBoardAsync(project, boardId, ct) is null) throw new BoardValidationException("Board not found.");
        if (!client.IsConfigured) return new(false, false, null, null);
        var link = await LinkAsync(project, boardId, ct);
        var descriptor = await client.DescribeAsync(link.RemoteBoardId, ct, link.DestinationKey)
            ?? throw new BoardValidationException("Board sharing requires the updated viberails.ai server.");
        var sharing = descriptor.IsOwner ? await client.GetSharingAsync(link.RemoteBoardId, ct, link.DestinationKey) : null;
        return new(true, descriptor.IsOwner, descriptor.RemoteBoardId, sharing);
    }

    /// <summary>Creates or updates an invitation through the hosted owner check.</summary>
    public async Task<BoardSharingResult> SaveAsync(string project, string boardId, string? inviteId, BoardSharingEmailRequest body, CancellationToken ct)
    {
        if (body.Email is not { Length: > 0 and <= 320 }) throw new BoardValidationException("Enter an email address up to 320 characters.");
        var link = await LinkAsync(project, boardId, ct);
        await client.SaveInviteAsync(link.RemoteBoardId, inviteId, body, ct, link.DestinationKey);
        return new("Invitation saved. The recipient can review it in Boards if eligible.");
    }

    /// <summary>Revokes an invitation through the hosted owner check.</summary>
    public async Task<BoardSharingResult> RemoveAsync(string project, string boardId, string inviteId, CancellationToken ct)
    {
        var link = await LinkAsync(project, boardId, ct);
        await client.RemoveInviteAsync(link.RemoteBoardId, inviteId, ct, link.DestinationKey);
        return new(Saved: true);
    }

    private async Task<BoardSyncLinkRecord> LinkAsync(string project, string boardId, CancellationToken ct)
    {
        if (await store.GetBoardAsync(project, boardId, ct) is null) throw new BoardValidationException("Board not found.");
        var destination = client.DestinationKey ?? throw new BoardValidationException("Sign in to share a board.");
        var link = await store.GetSyncLinkAsync(project, boardId, ct);
        if (link is null || (!link.Imported && link.DestinationKey != destination))
        {
            await sync.SyncNowAsync(project, boardId, ct);
            link = await store.GetSyncLinkAsync(project, boardId, ct);
        }
        if (link is null || link.DestinationKey != destination)
            throw new BoardValidationException("This board is not linked to the signed-in account.");
        return link;
    }
}
