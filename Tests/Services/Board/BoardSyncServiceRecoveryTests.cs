using VibeRails.Services.Board;
using VibeRails.Services.Board.Sync;
using VibeRails.Services.Diagnostics;
using Xunit;

namespace Tests.Services.Board;

public sealed partial class BoardSyncServiceTests
{
    [Fact]
    public async Task RecoveryRetriesTheSamePageAfterRateLimiting()
    {
        var card = await Card();
        await service.SetPublishedAsync(root, card.BoardId, true, Ct);
        client.Entries.Clear();
        client.RateLimitedRecoveryPulls = 1;
        var clock = new ImmediateRecoveryClock();
        var recovering = new BoardSyncService(store, client, new BoardSyncLock(Path.Combine(root, "sync.lock")),
            NullFeatureLog.Instance, activityCache, clock);

        var status = await recovering.SyncNowAsync(root, card.BoardId, Ct);
        Assert.Null(status!.LastError);
        Assert.Equal(0, status.Unsent);
        Assert.Equal(TimeSpan.FromSeconds(2), Assert.Single(clock.Delays));
    }

    private sealed class ImmediateRecoveryClock : TimeProvider
    {
        public List<TimeSpan> Delays { get; } = [];
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Delays.Add(dueTime);
            return base.CreateTimer(callback, state, TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        }
    }

    [Fact]
    public async Task EmptyRestoredServerRecoversEvenWithoutANewLocalEdit()
    {
        var card = await Card();
        await service.SetPublishedAsync(root, card.BoardId, true, Ct);
        client.Entries.Clear();

        var status = await service.SyncNowAsync(root, card.BoardId, Ct);
        Assert.Null(status!.LastError);
        Assert.Equal(0, status.Unsent);
        Assert.Equal("Local", client.Entries.Last(e => e.Kind == "change").Changes!.Value.GetProperty("title").GetProperty("to").GetString());
    }

    [Fact]
    public async Task OlderRemoteDeletionCannotUndoTheRecoveredLocalLiveness()
    {
        var card = await Card();
        await service.SetPublishedAsync(root, card.BoardId, true, Ct);
        var link = (await store.GetSyncLinkAsync(root, card.BoardId, Ct))!;
        await store.RecordSkippedSyncEntryAsync(card.BoardId, new("log_old_skip", 1, card.Key, "change", "Old history"), "1.0.0", Ct);
        await store.ReconcileOwnedSyncHistoryAsync(link, [], 0, Ct);
        var snapshots = await store.GetUnsentLogEntriesAsync(card.BoardId, 100, Ct);
        await store.MarkLogEntriesSentAsync(snapshots.Select((e, i) => new KeyValuePair<string, long>(e.Entry.Id, i + 10)).ToList(), Ct);

        Assert.True(await store.DeleteSyncedCardAsync(root, card.Id, BoardAuthor.User(),
            new("log_older_delete", 5, DateTime.UtcNow, "Older deletion", BoardId: card.BoardId), Ct));
        Assert.NotNull(await store.FindCardAsync(root, card.Id, Ct));
        Assert.True(await store.HasLogEntryAsync("log_older_delete", Ct));
        Assert.Equal(0, await store.CountSkippedSyncEntriesAsync(card.BoardId, Ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoveryKeepsCurrentLocalFieldsOverRestoredWebState(bool loseRecoveryAck)
    {
        var card = await Card();
        await service.SetPublishedAsync(root, card.BoardId, true, Ct);
        await store.UpdateCardAsync(root, card.Id, new(Title: "Local wins", Description: "Latest local description"), Ct);
        await service.SyncNowAsync(root, card.BoardId, Ct);
        client.Entries.RemoveAt(1);
        await store.AddCommentAsync(root, card.Id, BoardAuthor.User(), "Later local discussion", Ct);
        client.BeforePull = () =>
        {
            client.Web(card, "change", "Concurrent web field", """{"title":{"to":"Website"}}""");
            client.LoseNextAck = loseRecoveryAck;
            return Task.CompletedTask;
        };
        var status = await service.SyncNowAsync(root, card.BoardId, Ct);
        if (loseRecoveryAck)
        {
            Assert.NotNull(status!.LastError);
            status = await service.SyncNowAsync(root, card.BoardId, Ct);
        }
        Assert.Null(status!.LastError);
        Assert.Equal(0, status.Unsent);
        Assert.Equal("Local wins", (await store.FindCardAsync(root, card.Id, Ct))!.Title);
        var lastFields = client.Entries.Last(e => e.Kind == "change").Changes!.Value;
        Assert.Equal("Local wins", lastFields.GetProperty("title").GetProperty("to").GetString());
        Assert.Equal("Latest local description", lastFields.GetProperty("description").GetProperty("to").GetString());
    }

    [Fact]
    public async Task MalformedRecoveryHistoryLeavesReceiptsAndContentUntouched()
    {
        var card = await Card();
        await service.SetPublishedAsync(root, card.BoardId, true, Ct);
        await store.UpdateCardAsync(root, card.Id, new(Title: "Keep locally"), Ct);
        await service.SyncNowAsync(root, card.BoardId, Ct);
        client.Entries.RemoveAt(1);
        await store.AddCommentAsync(root, card.Id, BoardAuthor.User(), "Pending", Ct);
        client.TransformPull = r => r with { Entries = [r.Entries[0] with { CardId = "invalid" }] };

        var status = await service.SyncNowAsync(root, card.BoardId, Ct);
        Assert.NotNull(status!.LastError);
        Assert.Equal(1, status.Unsent);
        Assert.Equal(2, status.Cursor);
        Assert.Equal(2, await store.GetMaxAcknowledgedSequenceAsync(card.BoardId, Ct));
        Assert.Equal("Keep locally", (await store.FindCardAsync(root, card.Id, Ct))!.Title);
    }

    [Fact]
    public async Task ImportedBoardNeverUsesOwnerRecovery()
    {
        var card = await Card();
        await service.SetPublishedAsync(root, card.BoardId, true, Ct);
        await store.UpdateCardAsync(root, card.Id, new(Title: "Acknowledged"), Ct);
        await service.SyncNowAsync(root, card.BoardId, Ct);
        await ExecuteAsync($"INSERT INTO BoardSharedOrigins VALUES ('{card.BoardId}', '{client.RemoteId:D}', 'server-and-account', 'VB');");
        client.Entries.RemoveAt(1);
        await store.AddCommentAsync(root, card.Id, BoardAuthor.User(), "Pending", Ct);
        var pullCalls = client.PullAfters.Count;

        var status = await service.SyncNowAsync(root, card.BoardId, Ct);
        Assert.Contains("conflicts with this device's recorded sync history", status!.LastError);
        Assert.Equal(1, status.Unsent);
        Assert.Equal(pullCalls, client.PullAfters.Count);
    }

    [Fact]
    public async Task ReconciliationPreservesLocalOnlyAndRejectedRowsAndOtherBoards()
    {
        var card = await Card();
        await service.SetPublishedAsync(root, card.BoardId, true, Ct);
        var privateEntry = await store.AddCommentAsync(root, card.Id, BoardAuthor.User(), "Local-only metadata", Ct);
        await store.MarkLogEntriesSentAsync([new(privateEntry!.Id, 0)], Ct);
        await store.UpdateCardAsync(root, card.Id, new(Title: "Rejected local value"), Ct);
        var rejected = Assert.Single(await store.GetUnsentLogEntriesAsync(card.BoardId, 20, Ct));
        Assert.True(await store.RejectLogEntryAsync(card.BoardId, rejected.Entry.Id, Ct));
        var other = await store.CreateBoardAsync(root, "Other", Ct);
        var otherCard = await store.CreateCardAsync(root, new(null, "Other card", "", null, "medium", null, [], false, BoardId: other.Id), Ct);
        var otherUnsent = Assert.Single(await store.GetUnsentLogEntriesAsync(other.Id, 20, Ct));
        var link = (await store.GetSyncLinkAsync(root, card.BoardId, Ct))!;

        var repaired = await store.ReconcileOwnedSyncHistoryAsync(link, [], 0, Ct);
        Assert.NotNull(repaired);
        Assert.Equal(1, await store.CountRejectedLogEntriesAsync(card.BoardId, Ct));
        var queued = await store.GetUnsentLogEntriesAsync(card.BoardId, 100, Ct);
        Assert.DoesNotContain(queued, e => e.Entry.Id == privateEntry.Id || e.Entry.Id == rejected.Entry.Id);
        Assert.Equal(otherUnsent.Entry.Id, Assert.Single(await store.GetUnsentLogEntriesAsync(other.Id, 20, Ct)).Entry.Id);
        Assert.Equal("Other card", (await store.FindCardAsync(root, otherCard.Id, Ct))!.Title);
        Assert.Equal("Rejected local value", (await store.FindCardAsync(root, card.Id, Ct))!.Title);
    }

    [Fact]
    public async Task RecoveryRetainsDeletedCardsAndRestoresLocallyLiveCardsOnTheServer()
    {
        var live = await Card();
        var deleted = await Card();
        await service.SetPublishedAsync(root, live.BoardId, true, Ct);
        await store.DeleteCardAsync(root, deleted.Id, Ct);
        await service.SyncNowAsync(root, live.BoardId, Ct);
        client.Entries.RemoveAt(2);
        await store.AddCommentAsync(root, live.Id, BoardAuthor.User(), "Trigger recovery", Ct);

        var status = await service.SyncNowAsync(root, live.BoardId, Ct);
        Assert.Null(status!.LastError);
        Assert.Null(await store.FindCardAsync(root, deleted.Id, Ct));
        Assert.NotNull(await store.FindCardAsync(root, live.Id, Ct));
        Assert.Equal("deleted", client.Entries.Last(e => e.CardId == deleted.Id).Kind);
        Assert.Equal("restored", client.Entries.Last(e => e.CardId == live.Id).Kind);
    }

    [Fact]
    public async Task RecoveryOutboxSurvivesBoundedTicksBeforePullResumes()
    {
        var card = await Card();
        for (var i = 0; i < 170; i++) await Card();
        await service.SetPublishedAsync(root, card.BoardId, true, Ct);
        await store.UpdateCardAsync(root, card.Id, new(Title: "Current local"), Ct);
        await service.SyncNowAsync(root, card.BoardId, Ct);
        client.Entries.RemoveAt(client.Entries.Count - 1);
        await store.AddCommentAsync(root, card.Id, BoardAuthor.User(), "Trigger recovery", Ct);
        var pullCalls = client.PullAfters.Count;
        var first = await service.SyncNowAsync(root, card.BoardId, Ct);
        Assert.Null(first!.LastError);
        Assert.True(first.Unsent > 0);
        // Only the full-history recovery read ran; normal pull waits for final local snapshots.
        Assert.Equal(pullCalls + 1, client.PullAfters.Count);
        var second = await service.SyncNowAsync(root, card.BoardId, Ct);
        Assert.Null(second!.LastError);
        Assert.Equal(0, second.Unsent);
        Assert.Equal("Current local", (await store.FindCardAsync(root, card.Id, Ct))!.Title);
    }
}
