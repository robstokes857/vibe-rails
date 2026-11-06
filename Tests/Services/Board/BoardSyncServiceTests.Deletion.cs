using Xunit;

namespace Tests.Services.Board;

public sealed partial class BoardSyncServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PermanentDeletionAndChanges_RetryInInsertionOrderAfterLostAcknowledgement(bool deletionFirst)
    {
        var kept = await Card();
        var gone = await Card();
        await service.SetPublishedAsync(root, kept.BoardId, true, Ct);

        if (deletionFirst)
            Assert.True(await store.DeleteCardAsync(root, gone.Id, Ct));
        else
            await store.UpdateCardAsync(root, kept.Id, new(Title: "Accepted before disconnect"), Ct);
        client.LoseNextAck = true;
        Assert.NotNull((await service.SyncNowAsync(root, kept.BoardId, Ct))!.LastError);

        if (deletionFirst)
            await store.UpdateCardAsync(root, kept.Id, new(Title: "Written after disconnect"), Ct);
        else
            Assert.True(await store.DeleteCardAsync(root, gone.Id, Ct));

        var queue = await store.GetUnsentLogEntriesAsync(kept.BoardId, 20, Ct);
        Assert.Equal(deletionFirst ? ["deleted", "change"] : new[] { "change", "deleted" },
            queue.Select(entry => entry.Entry.Kind));
        var retried = (await service.SyncNowAsync(root, kept.BoardId, Ct))!;
        Assert.Null(retried.LastError);
        Assert.Equal(0, retried.Unsent);
        Assert.Equal(4, client.Entries.Count);
        Assert.Null(await store.FindCardAsync(root, gone.Id, Ct));
    }
}
