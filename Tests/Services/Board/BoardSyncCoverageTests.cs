using System.Text.Json;
using VibeRails.Services.Board;
using VibeRails.Services.Board.Sync;
using VibeRails.Services.Diagnostics;
using Xunit;

namespace Tests.Services.Board;

/// <summary>
/// SYNC.md guarantees that <see cref="BoardSyncServiceTests"/> left unpinned: the per-tick page
/// caps, consent re-approval after a destination change, re-publishing to a different remote
/// board, rejections across a pause, and deleting a published local board. Same fixture shape:
/// a temp-file store, a fake client, and the file lock beside them.
/// </summary>
public sealed class BoardSyncCoverageTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "board-sync-coverage-" + Guid.NewGuid().ToString("N"));
    private readonly BoardStore store;
    private readonly FakeClient client = new();
    private readonly BoardSyncService service;
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public BoardSyncCoverageTests()
    {
        Directory.CreateDirectory(root);
        var cs = $"Data Source={Path.Combine(root, "board.db")};Pooling=False";
        store = new BoardStore(cs, cs);
        service = new(store, client, new BoardSyncLock(Path.Combine(root, "sync.lock")), NullFeatureLog.Instance);
    }

    private async Task<BoardCardRecord> Card()
    {
        await store.EnsureDefaultColumnsAsync(root, Ct);
        return await store.CreateCardAsync(root, new(null, "Local", "description", null, "medium", null, [], false), Ct);
    }

    [Fact]
    public async Task OneTickPullsAtMostTwentyFivePagesOfTwenty_AndTheNextTickResumesFromTheCursor()
    {
        var card = await Card();
        await service.SetPublishedAsync(root, card.BoardId, true, Ct); // seq 1 is the creation entry
        for (var index = 0; index < 520; index++)
            client.Web(card, "comment", "Web comment " + index, null); // seq 2..521, always "more"
        client.Pulls = 0;

        var status = await service.SyncNowAsync(root, card.BoardId, Ct);
        Assert.Null(status!.LastError);
        Assert.Equal(25, client.Pulls);
        Assert.Equal(501, status.Cursor);
        Assert.Equal(500, (await store.GetCardDetailAsync(root, card.Id, Ct))!.Comments.Count);
        Assert.True(await store.HasLogEntryAsync(client.Entries[500].Id, Ct));   // seq 501: last of the tick
        Assert.False(await store.HasLogEntryAsync(client.Entries[501].Id, Ct));  // seq 502: next tick

        var rest = await service.SyncNowAsync(root, card.BoardId, Ct);
        Assert.Null(rest!.LastError);
        Assert.Equal(26, client.Pulls);
        Assert.Equal(521, rest.Cursor);
        Assert.Equal(520, (await store.GetCardDetailAsync(root, card.Id, Ct))!.Comments.Count);
    }

    [Fact]
    public async Task OneTickPushesAtMostTwentyFiveBatchesOfTwenty_AndTheRestGoNextTick()
    {
        var card = await Card();
        for (var index = 0; index < 520; index++)
            await store.AddCommentAsync(root, card.Id, BoardAuthor.User(), "Local comment " + index, Ct);

        var status = await service.SetPublishedAsync(root, card.BoardId, true, Ct);
        Assert.Null(status!.LastError);
        Assert.Equal(25, client.Pushes);
        Assert.Equal(500, client.Entries.Count);
        Assert.Equal("created", client.Entries[0].Kind);
        Assert.Equal(21, status.Unsent);
        Assert.Equal(500, status.Cursor);

        var rest = await service.SyncNowAsync(root, card.BoardId, Ct);
        Assert.Null(rest!.LastError);
        Assert.Equal(27, client.Pushes); // 20 + 1: a short batch ends the loop
        Assert.Equal(521, client.Entries.Count);
        Assert.Equal(0, rest.Unsent);
        Assert.Equal(521, rest.Cursor);
    }

    [Fact]
    public async Task ChangedDestinationStopsUploads_UntilPublishingIsSwitchedOffAndOnAgain()
    {
        var card = await Card();
        await service.SetPublishedAsync(root, card.BoardId, true, Ct);
        client.DestinationKey = "rotated-key-or-other-server";
        await store.UpdateCardAsync(root, card.Id, new(Title: "Edited after the rotation"), Ct);

        var calls = client.Calls;
        await service.SyncDueAsync(Ct);
        var stopped = (await service.GetStatusAsync(root, card.BoardId, Ct))!;
        Assert.Contains("server or API key changed", stopped.LastError);
        Assert.True(stopped.Enabled);
        Assert.Equal(1, stopped.Unsent);
        Assert.Equal(calls, client.Calls);
        Assert.Single(client.Entries);

        // Off and on again is the consent step; nothing else re-approves.
        var paused = (await service.SetPublishedAsync(root, card.BoardId, false, Ct))!;
        Assert.False(paused.Enabled);
        Assert.Equal(calls, client.Calls);
        var approved = (await service.SetPublishedAsync(root, card.BoardId, true, Ct))!;
        Assert.True(approved.Enabled);
        Assert.Null(approved.LastError);
        Assert.Equal(0, approved.Unsent);
        Assert.Equal(2, client.Entries.Count);
        Assert.Equal("rotated-key-or-other-server", (await store.GetSyncLinkAsync(root, card.BoardId, Ct))!.DestinationKey);
        Assert.Null((await service.SyncNowAsync(root, card.BoardId, Ct))!.LastError);
    }

    [Fact]
    public async Task PublishingToADifferentRemoteBoardResendsEveryEntryFromAFreshCursor()
    {
        var card = await Card();
        await store.AddCommentAsync(root, card.Id, BoardAuthor.User(), "Before the move", Ct);
        var first = (await service.SetPublishedAsync(root, card.BoardId, true, Ct))!;
        Assert.Equal(0, first.Unsent);
        Assert.Equal(2, first.Cursor);
        var sent = client.Entries.Select(e => e.Id).ToList();

        // The key now names a different account or board on the server, whose copy is empty.
        client.RemoteId = Guid.NewGuid();
        client.Entries.Clear();
        var second = (await service.SetPublishedAsync(root, card.BoardId, true, Ct))!;
        Assert.Equal(client.RemoteId.ToString("D"), second.RemoteBoardId);
        Assert.NotEqual(first.RemoteBoardId, second.RemoteBoardId);
        Assert.Null(second.LastError);
        Assert.Equal(0, second.Unsent);
        Assert.Equal(2, second.Cursor);
        Assert.Equal(sent, client.Entries.Select(e => e.Id));
        Assert.Equal(new long[] { 1, 2 }, client.Entries.Select(e => e.Seq));
    }

    [Fact]
    public async Task PausingRetainsTheRejectedCountAndIdentities_AndResumingKeepsThem()
    {
        var card = await Card();
        var creation = Assert.Single(await store.GetUnsentLogEntriesAsync(card.BoardId, 20, Ct));
        client.RejectEntryId = creation.Entry.Id;
        var published = (await service.SetPublishedAsync(root, card.BoardId, true, Ct))!;
        Assert.Null(published.LastError);
        Assert.Equal(1, published.Rejected);
        Assert.Equal(creation.Entry.Id, Assert.Single(published.RejectedEntries!).EntryId);

        var paused = (await service.SetPublishedAsync(root, card.BoardId, false, Ct))!;
        Assert.False(paused.Enabled);
        Assert.True(paused.Published);
        Assert.Equal(1, paused.Rejected);
        var identity = Assert.Single(paused.RejectedEntries!);
        Assert.Equal(creation.Entry.Id, identity.EntryId);
        Assert.Equal(card.Key, identity.CardKey);
        Assert.Equal("created", identity.Kind);
        Assert.Equal(1, (await service.GetStatusAsync(root, card.BoardId, Ct))!.Rejected);

        client.RejectEntryId = null;
        var resumed = (await service.SetPublishedAsync(root, card.BoardId, true, Ct))!;
        Assert.Null(resumed.LastError);
        Assert.True(resumed.Enabled);
        Assert.Equal(1, resumed.Rejected);
        Assert.Equal(creation.Entry.Id, Assert.Single(resumed.RejectedEntries!).EntryId);
        // A retained rejection is not resent on resume; the card needs a replacement.
        Assert.Empty(client.Entries);
    }

    [Fact]
    public async Task DeletingTheLocalBoardEndsItsSync_WithoutTouchingThePublishedCopy()
    {
        var card = await Card();
        await service.SetPublishedAsync(root, card.BoardId, true, Ct);
        var remoteEntries = client.Entries.Count;
        Assert.Single(await store.GetSyncLinksAsync(Ct));

        await store.CreateBoardAsync(root, "Survivor", Ct);
        Assert.NotNull(await store.DeleteBoardAsync(root, card.BoardId, Ct));
        Assert.Empty(await store.GetSyncLinksAsync(Ct));
        Assert.Null(await service.GetStatusAsync(root, card.BoardId, Ct));

        var calls = client.Calls;
        await service.SyncDueAsync(Ct);
        Assert.Equal(calls, client.Calls);
        Assert.Equal(remoteEntries, client.Entries.Count);
    }

    private sealed class FakeClient : IBoardSyncClient
    {
        public bool IsConfigured => true;
        public string? DestinationKey { get; set; } = "server-and-account";
        public int Calls, Pushes, Pulls;
        public string? RejectEntryId;
        public Guid RemoteId = Guid.NewGuid();
        public List<BoardSyncPulledEntryWire> Entries { get; } = [];
        public Task<BoardSyncActivityAck> PutActivityAsync(string boardId, string cardId, BoardSyncActivityWire activity, CancellationToken ct, string? expectedDestination = null)
        {
            Calls++;
            return Task.FromResult(new BoardSyncActivityAck(1, cardId));
        }

        public Task<BoardSyncPublishResponse> PublishAsync(BoardSyncPublishRequest request, CancellationToken ct, string? expectedDestination = null)
        {
            Calls++;
            return Task.FromResult(new BoardSyncPublishResponse(RemoteId, request.Name, Entries.Count));
        }

        public Task<BoardSyncPushResponse> PushAsync(string boardId, BoardSyncPushRequest request, CancellationToken ct, string? expectedDestination = null)
        {
            Calls++;
            Pushes++;
            if (RejectEntryId is { } rejected && request.Entries.Any(e => e.Id == rejected))
                throw new BoardSyncClientException("Rejected", "invalid_entry", 400, rejected);
            var accepted = new List<BoardSyncAcceptedWire>();
            foreach (var entry in request.Entries)
            {
                var stored = Entries.FirstOrDefault(e => e.Id == entry.Id);
                if (stored is null)
                {
                    stored = new(entry.Id, entry.CardId, entry.CardKey, entry.Kind, entry.Author, entry.Body, entry.Changes, entry.CreatedUtc, Entries.Count + 1, "desktop");
                    Entries.Add(stored);
                }
                accepted.Add(new(entry.Id, stored.Seq));
            }
            return Task.FromResult(new BoardSyncPushResponse(accepted, Entries.Count));
        }

        public Task<BoardSyncPullResponse> PullAsync(string boardId, long after, int limit, CancellationToken ct, string? expectedDestination = null)
        {
            Calls++;
            Pulls++;
            return Task.FromResult(new BoardSyncPullResponse(
                Entries.Where(e => e.Seq > after).Take(limit).ToList(), Entries.Count, Entries.Count - after > limit));
        }

        public void Web(BoardCardRecord card, string kind, string body, string? changes) => Entries.Add(new(
            "log_" + Guid.NewGuid().ToString("N"), card.Id, card.Key, kind, new("user", "Web", null), body,
            changes is null ? null : JsonDocument.Parse(changes).RootElement.Clone(), DateTime.UtcNow, Entries.Count + 1, "web"));
    }

    public void Dispose()
    {
        // A failed test can leave the lock file open briefly; never mask the failure with an IOException.
        try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
