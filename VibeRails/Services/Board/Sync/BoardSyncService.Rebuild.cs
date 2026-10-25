namespace VibeRails.Services.Board.Sync;

public sealed partial class BoardSyncService
{
    private static void RequireRebuildOwner(BoardSyncLinkRecord link, BoardRemoteDescriptor remote)
    {
        if (link.Imported || !remote.IsOwner
            || !string.Equals(remote.RemoteBoardId, link.RemoteBoardId, StringComparison.OrdinalIgnoreCase))
            throw new BoardValidationException("Only the local owner's exact hosted board can be rebuilt; local work is retained.");
    }

    private async Task<BoardSyncLinkRecord> ContinueOwnedRebuildAsync(BoardSyncLinkRecord link,
        BoardSyncRebuildRecord rebuild, CancellationToken ct)
    {
        if (link.Imported || client.DestinationKey != rebuild.DestinationKey || link.DestinationKey != rebuild.DestinationKey
            || link.RemoteBoardId != (rebuild.ReplacementRemoteBoardId ?? rebuild.RemoteBoardId))
            throw new BoardValidationException("The board or account changed during recovery; local work is retained.");
        var board = await store.GetBoardAsync(link.ProjectPath, link.BoardId, ct);
        if (board is not { SyncEnabled: true })
            throw new BoardValidationException("Hosted-copy recovery is paused while this board's sync is off.");
        if (rebuild.Phase == "upload") return link;
        if (rebuild.Phase is not ("delete" or "publish"))
            throw new BoardValidationException("The hosted-copy recovery phase is invalid; local work is retained.");

        if (rebuild.Phase == "delete")
        {
            BoardRemoteDescriptor? remote;
            try { remote = await client.DescribeAsync(rebuild.RemoteBoardId, ct, rebuild.DestinationKey); }
            catch (BoardSyncClientException ex) when (ex.Code == BoardSyncWire.CodeBoardNotFound) { remote = null; }
            if (remote is not null)
            {
                RequireRebuildOwner(link, remote);
                await client.DeleteRemoteBoardAsync(rebuild.RemoteBoardId, ct, rebuild.DestinationKey);
            }
            else
            {
                // A null descriptor alone is not proof of deletion (older clients use null for
                // unsupported). Only an explicit 404 from the existing API is authoritative.
                try
                {
                    await client.PullAsync(rebuild.RemoteBoardId, 0, 1, ct, rebuild.DestinationKey);
                    throw new BoardValidationException("The hosted board's owner could not be verified; recovery will retry.");
                }
                catch (BoardSyncClientException ex) when (ex.Code == BoardSyncWire.CodeBoardNotFound) { }
            }
            await store.ConfirmSyncRebuildDeletedAsync(rebuild.Id, ct);
        }

        // Publish is idempotent by this local board's permanent identity. If its response is
        // lost, the durable publish phase repeats that request, never the confirmed deletion.
        var layout = await ReadLayoutAsync(link.ProjectPath, board, ct);
        var published = await client.PublishAsync(new(board.Id, board.Name, layout.Prefix, layout.Lanes,
            board.EffectiveDisplayPrefix, SourceRepository(board.ProjectPath), SourceComputer()), ct, rebuild.DestinationKey);
        var replacement = await store.PrepareSyncRebuildUploadAsync(rebuild, published.RemoteBoardId.ToString("D"), ct)
            ?? throw new BoardValidationException("The local board changed during recovery; recovery will retry.");
        featureLog.Write(FeatureName, "rebuild", "Replaced the conflicting hosted copy; uploading the local owner's board.",
            link.BoardId, replacement.RemoteBoardId, "uploading");
        return replacement;
    }
}
