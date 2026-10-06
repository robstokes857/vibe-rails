namespace VibeRails.Services.Board.Sync;

/// <summary>Desktop sharing orchestration. Hosted API checks owner/member permissions on every call.</summary>
public sealed class BoardSharingService(IBoardStore store, IBoardSyncClient client, IBoardSyncService sync, BoardSyncLock syncLock)
{
    /// <summary>Loads hosted ownership before returning collaborator controls.</summary>
    public async Task<BoardSharingState> GetAsync(string project, string boardId, CancellationToken ct)
    {
        if (await store.GetBoardAsync(project, boardId, ct) is null) throw new BoardValidationException("Board not found.");
        // Sharing lives on the production board copy; a local Front process shows it as unavailable.
        if (!client.IsConfigured || LocalFront.LocalFrontMode.PausesProductionPublishing) return new(false, false, null, null);
        var link = await LinkAsync(project, boardId, ct);
        var descriptor = await client.DescribeAsync(link.RemoteBoardId, ct, link.DestinationKey)
            ?? throw new BoardValidationException("Board sharing requires the updated viberails.ai server.");
        var sharing = descriptor.IsOwner ? await client.GetSharingAsync(link.RemoteBoardId, ct, link.DestinationKey) : null;
        return new(true, descriptor.IsOwner, descriptor.RemoteBoardId, sharing);
    }

    /// <summary>Creates or updates an invitation through the hosted owner check.</summary>
    public async Task<BoardSharingResult> SaveAsync(string project, string boardId, string? inviteId, BoardSharingEmailRequest body, CancellationToken ct)
    {
        BoardSyncService.ThrowIfPausedForLocalFront();
        if (body.Email is not { Length: > 0 and <= 320 }) throw new BoardValidationException("Enter an email address up to 320 characters.");
        var link = await LinkAsync(project, boardId, ct);
        await client.SaveInviteAsync(link.RemoteBoardId, inviteId, body, ct, link.DestinationKey);
        return new("Invitation saved. The recipient can review it in Boards if eligible.");
    }

    /// <summary>Revokes an invitation through the hosted owner check.</summary>
    public async Task<BoardSharingResult> RemoveAsync(string project, string boardId, string inviteId, CancellationToken ct)
    {
        BoardSyncService.ThrowIfPausedForLocalFront();
        var link = await LinkAsync(project, boardId, ct);
        await client.RemoveInviteAsync(link.RemoteBoardId, inviteId, ct, link.DestinationKey);
        return new(Saved: true);
    }

    /// <summary>Lists accepted shared boards for the configured account.</summary>
    public async Task<IReadOnlyList<BoardRemoteDescriptor>> DiscoverAsync(CancellationToken ct)
    {
        if (LocalFront.LocalFrontMode.PausesProductionPublishing) return [];
        var destination = client.DestinationKey ?? throw new BoardValidationException("Sign in to viberails.ai to see shared boards.");
        return (await client.DiscoverAsync(ct, destination)).Where(b => !b.IsOwner).Take(100).ToList();
    }

    /// <summary>Imports an accepted board into the current project and starts bounded sync.</summary>
    public async Task<BoardSharedImportResult> ImportAsync(string project, string remoteId, CancellationToken ct)
    {
        BoardSyncService.ThrowIfPausedForLocalFront();
        BoardRecord board;
        using (var held = syncLock.TryAcquire() ?? throw new BoardValidationException("A Board sync is running. Try again when it finishes."))
        {
            var destination = client.DestinationKey ?? throw new BoardValidationException("Sign in before adding a shared board.");
            var remote = await client.DescribeAsync(remoteId, ct, destination) ?? throw new BoardValidationException("Shared board not found.");
            if (!string.Equals(remoteId, remote.RemoteBoardId, StringComparison.OrdinalIgnoreCase) || client.DestinationKey != destination)
                throw new BoardValidationException("The board or account changed. Please retry.");
            board = await store.ImportSharedBoardAsync(project, remote, destination, ct);
        }
        // The ordinary bounded sync fills the board and carries later local edits back.
        var status = await sync.SyncNowAsync(project, board.Id, ct);
        return new(board.Id, board.Name, status?.LastError);
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
