using VibeRails.Services.Board;
using VibeRails.Services.Board.Sync;
using VibeRails.Services.Diagnostics;
using Xunit;

namespace Tests.Services.Board;

public sealed partial class BoardSyncServiceTests
{
    private async Task<BoardCardRecord> ConflictingOwnedBoard()
    {
        var card = await Card();
        await service.SetPublishedAsync(root, card.BoardId, true, Ct);
        await store.UpdateCardAsync(root, card.Id, new(Title: "Local host wins"), Ct);
        await service.SyncNowAsync(root, card.BoardId, Ct);
        client.Entries.RemoveAt(1);
        await store.AddCommentAsync(root, card.Id, BoardAuthor.User(), "Retain local discussion", Ct);
        client.SupportsOwnedRebuild = true;
        return card;
    }

    [Fact]
    public async Task ConfirmedHistoryConflictReplacesRemoteWithLocalHostState()
    {
        var card = await ConflictingOwnedBoard();
        var oldRemote = client.RemoteId;
        var oldEntry = client.Entries[0].Id;
        var status = await service.SyncNowAsync(root, card.BoardId, Ct);

        Assert.Null(status!.LastError);
        Assert.Equal(1, client.RemoteDeletes);
        Assert.NotEqual(oldRemote, client.RemoteId);
        Assert.Equal(client.RemoteId.ToString("D"), status.RemoteBoardId);
        Assert.Equal(0, status.Unsent);
        Assert.Null(await store.GetPendingSyncRebuildAsync(card.BoardId, Ct));
        Assert.True(await store.HasLogEntryAsync(oldEntry, Ct));
        Assert.Equal("Local host wins", (await store.FindCardAsync(root, card.Id, Ct))!.Title);
        Assert.Equal("Local host wins", client.Entries.Last(e => e.Kind == "change").Changes!.Value.GetProperty("title").GetProperty("to").GetString());
        Assert.Equal(card.CreatedUtc, client.Entries.First(e => e.CardId == card.Id && e.Kind == "created").CreatedUtc);
        Assert.Contains(client.Entries, e => e.Kind == "comment" && e.Body == "Retain local discussion");
        Assert.NotEmpty(client.Activity);
    }

    [Fact]
    public async Task Persistent409WithRegressedHistoryRebuildsInsteadOfRetryingForever()
    {
        var card = await ConflictingOwnedBoard();
        client.PushConflictUntilDelete = true;
        var status = await service.SyncNowAsync(root, card.BoardId, Ct);
        Assert.Null(status!.LastError);
        Assert.Equal(1, client.RemoteDeletes);
        Assert.Equal(0, status.Unsent);
    }

    [Theory]
    [InlineData("network")]
    [InlineData(BoardSyncWire.CodeWriteConflict)]
    public async Task NetworkFailuresAndOrdinaryContentionNeverDeleteTheRemote(string code)
    {
        var card = await Card();
        await service.SetPublishedAsync(root, card.BoardId, true, Ct);
        client.SupportsOwnedRebuild = true;
        client.PushError = new BoardSyncClientException("Retry", code, code == "network" ? 0 : 409);
        var status = await service.SyncNowAsync(root, card.BoardId, Ct);
        Assert.NotNull(status!.LastError);
        Assert.Equal(0, client.RemoteDeletes);
        Assert.Null(await store.GetPendingSyncRebuildAsync(card.BoardId, Ct));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LostRemoteAcknowledgementsResumeAfterServiceRestart(bool lostDelete)
    {
        var card = await ConflictingOwnedBoard();
        client.LoseDeleteAck = lostDelete;
        client.LosePublishAck = !lostDelete;
        var first = await service.SyncNowAsync(root, card.BoardId, Ct);
        Assert.NotNull(first!.LastError);
        Assert.Equal(lostDelete ? "delete" : "publish", (await store.GetPendingSyncRebuildAsync(card.BoardId, Ct))!.Phase);
        var restarted = new BoardSyncService(store, client, new BoardSyncLock(Path.Combine(root, "sync.lock")),
            NullFeatureLog.Instance, activityCache);
        var completed = await restarted.SyncNowAsync(root, card.BoardId, Ct);
        Assert.Null(completed!.LastError);
        Assert.Equal(0, completed.Unsent);
        Assert.Equal(1, client.RemoteDeletes);
        Assert.Null(await store.GetPendingSyncRebuildAsync(card.BoardId, Ct));
    }

    [Fact]
    public async Task ChangedAccountCannotResumeAnUnfinishedRebuild()
    {
        var card = await ConflictingOwnedBoard();
        client.LoseDeleteAck = true;
        await service.SyncNowAsync(root, card.BoardId, Ct);
        client.DestinationKey = "another-account";
        await Assert.ThrowsAsync<BoardValidationException>(() => service.SyncNowAsync(root, card.BoardId, Ct));
        Assert.Equal(1, client.RemoteDeletes);
        Assert.Equal(1, client.Publications);
        Assert.Equal("delete", (await store.GetPendingSyncRebuildAsync(card.BoardId, Ct))!.Phase);
        Assert.Equal("Local host wins", (await store.FindCardAsync(root, card.Id, Ct))!.Title);
    }

    [Fact]
    public async Task NonOwnerCannotReplaceAConflictingRemote()
    {
        var card = await ConflictingOwnedBoard();
        client.RemoteIsOwner = false;
        var status = await service.SyncNowAsync(root, card.BoardId, Ct);
        Assert.Contains("Only the local owner", status!.LastError);
        Assert.Equal(0, client.RemoteDeletes);
        Assert.Null(await store.GetPendingSyncRebuildAsync(card.BoardId, Ct));
    }

    [Fact]
    public async Task RebuildUsesFreshCreationForPreviouslyRejectedCardsAndPreservesPrivateRows()
    {
        var card = await ConflictingOwnedBoard();
        var rejectedCard = await Card();
        var creation = (await store.GetUnsentLogEntriesAsync(card.BoardId, 100, Ct)).Single(e => e.Entry.CardId == rejectedCard.Id);
        await store.RejectLogEntryAsync(card.BoardId, creation.Entry.Id, Ct);
        await store.AddCommentAsync(root, rejectedCard.Id, BoardAuthor.User(), "Waiting behind rejected creation", Ct);
        var localOnly = await store.AddCommentAsync(root, card.Id, BoardAuthor.User(), "Private local row", Ct);
        await store.MarkLogEntriesSentAsync([new(localOnly!.Id, 0)], Ct);

        var status = await service.SyncNowAsync(root, card.BoardId, Ct);
        Assert.Null(status!.LastError);
        Assert.Equal(0, status.Rejected);
        Assert.Equal(0, status.Unsent);
        Assert.True(await store.HasLogEntryAsync(creation.Entry.Id, Ct));
        Assert.Contains(client.Entries, e => e.CardId == rejectedCard.Id && e.Kind == "created");
        Assert.Contains(client.Entries, e => e.Body == "Waiting behind rejected creation");
        Assert.DoesNotContain(client.Entries, e => e.Id == localOnly.Id);
        Assert.True(await store.HasLogEntryAsync(localOnly.Id, Ct));
    }

    [Fact]
    public async Task ImportedBoardCannotTriggerReplacementOfItsOwnersRemote()
    {
        var card = await ConflictingOwnedBoard();
        await ExecuteAsync($"INSERT INTO BoardSharedOrigins VALUES ('{card.BoardId}', '{client.RemoteId:D}', 'server-and-account', 'VB');");
        var status = await service.SyncNowAsync(root, card.BoardId, Ct);
        Assert.NotNull(status!.LastError);
        Assert.Equal(0, client.RemoteDeletes);
        Assert.Null(await store.GetPendingSyncRebuildAsync(card.BoardId, Ct));
    }

    [Fact]
    public async Task MalformedRecoveryPageCannotTriggerRemoteDeletion()
    {
        var card = await ConflictingOwnedBoard();
        client.TransformPull = response => response with { Entries = [response.Entries[0] with { CardId = "invalid" }] };
        Assert.NotNull((await service.SyncNowAsync(root, card.BoardId, Ct))!.LastError);
        Assert.Equal(0, client.RemoteDeletes);
        Assert.Null(await store.GetPendingSyncRebuildAsync(card.BoardId, Ct));
    }

    [Fact]
    public async Task ReplacementUploadResumesAcrossBoundedTicksWithoutRepeatingDeletion()
    {
        var card = await ConflictingOwnedBoard();
        var rows = string.Join(",", Enumerable.Range(0, 505).Select(i =>
            $"('cm_rebuild_{i}', '{card.Id}', 'user', 'Owner', 'Pending {i}', '2026-10-25T00:00:00Z', 'comment')"));
        await ExecuteAsync("INSERT INTO BoardComments (Id,CardId,AuthorKind,AuthorLabel,Body,CreatedUTC,Kind) VALUES " + rows + ";");
        var first = await service.SyncNowAsync(root, card.BoardId, Ct);
        Assert.Null(first!.LastError);
        Assert.True(first.Unsent > 0);
        Assert.Equal("upload", (await store.GetPendingSyncRebuildAsync(card.BoardId, Ct))!.Phase);
        Assert.Equal(1, client.RemoteDeletes);
        var restarted = new BoardSyncService(store, client, new BoardSyncLock(Path.Combine(root, "sync.lock")),
            NullFeatureLog.Instance, activityCache);
        var second = await restarted.SyncNowAsync(root, card.BoardId, Ct);
        Assert.Null(second!.LastError);
        Assert.Equal(0, second.Unsent);
        Assert.Null(await store.GetPendingSyncRebuildAsync(card.BoardId, Ct));
        Assert.Equal(1, client.RemoteDeletes);
        Assert.Equal(505, client.Entries.Count(e => e.Id.StartsWith("cm_rebuild_", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task RebuildJournalUpgradesAnExistingDatabaseWithoutChangingItsBoardHistory()
    {
        var card = await Card();
        var comment = await store.AddCommentAsync(root, card.Id, BoardAuthor.User(), "Existing history", Ct);
        await ExecuteAsync("DROP TABLE BoardSyncRebuilds; DELETE FROM SchemaMigrations WHERE Component='board-sync-rebuild';");
        var upgraded = new BoardStore(cs, cs);
        Assert.Null(await upgraded.GetPendingSyncRebuildAsync(card.BoardId, Ct));
        Assert.True(await upgraded.HasLogEntryAsync(comment!.Id, Ct));
        Assert.Equal(card.Title, (await upgraded.FindCardAsync(root, card.Id, Ct))!.Title);
    }

    [Fact]
    public async Task ExplicitLocalBoardDeletionIsNotBlockedByItsCompletedRebuildReceipt()
    {
        var card = await ConflictingOwnedBoard();
        Assert.Null((await service.SyncNowAsync(root, card.BoardId, Ct))!.LastError);
        await store.CreateBoardAsync(root, "Another board", Ct);
        Assert.NotNull(await store.DeleteBoardAsync(root, card.BoardId, Ct));
        Assert.Null(await store.GetBoardAsync(root, card.BoardId, Ct));
    }

    [Fact]
    public async Task RebuildingSourcePreservesDepartedCardsHistoryWithoutChangingDestination()
    {
        var card = await ConflictingOwnedBoard();
        var departed = await Card();
        var destination = await store.CreateBoardAsync(root, "Destination", Ct);
        var lane = (await store.GetColumnsAsync(root, Ct, destination.Id))[0];
        await store.MoveCardAsync(root, departed.Id, lane.Id, null, Ct);
        var destinationPending = (await store.GetUnsentLogEntriesAsync(destination.Id, 100, Ct)).Select(e => e.Entry.Id).ToArray();
        var status = await service.SyncNowAsync(root, card.BoardId, Ct);
        Assert.Null(status!.LastError);
        Assert.Contains(client.Entries, e => e.CardId == departed.Id && e.Kind == "created");
        Assert.Equal("deleted", client.Entries.Last(e => e.CardId == departed.Id).Kind);
        Assert.Equal(destination.Id, (await store.FindCardAsync(root, departed.Id, Ct))!.BoardId);
        Assert.Equal(destinationPending, (await store.GetUnsentLogEntriesAsync(destination.Id, 100, Ct)).Select(e => e.Entry.Id));
    }
}
