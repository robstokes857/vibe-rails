using System.Text.Json;
using Microsoft.Data.Sqlite;
using VibeRails.Services.Board;
using VibeRails.Services.Board.Sync;
using VibeRails.Services.Diagnostics;
using Xunit;

namespace Tests.Services.Board;

public sealed class BoardSharingTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "board-sharing-" + Guid.NewGuid().ToString("N"));
    private readonly BoardStore store;
    private readonly SharedServer server = new();
    private readonly BoardSyncActivityCache cache = new();
    private readonly BoardSyncService sync;
    private readonly BoardSharingService sharing;
    private readonly string cs;
    private CancellationToken Ct => TestContext.Current.CancellationToken;
    public BoardSharingTests()
    {
        Directory.CreateDirectory(root); cs = $"Data Source={Path.Combine(root, "board.db")};Pooling=False";
        store = new(cs, cs);
        var gate = new BoardSyncLock(Path.Combine(root, "sync.lock"));
        sync = new(store, server, gate, NullFeatureLog.Instance, cache);
        sharing = new(store, server, sync, gate);
        server.Event("created", """{"title":{"to":"Owner card"},"lane":{"to":"col_shared"},"description":{"to":"Shared text"}}""");
    }

    [Fact]
    public async Task ImportRetainsIdentityAndSupportsTwoWayCardsAndCommentsWithoutRepublishing()
    {
        var imported = await sharing.ImportAsync(root, server.Remote.RemoteBoardId, Ct);
        var card = (await store.FindCardAsync(root, "card_000000000001", Ct))!;
        Assert.Equal("VB-1", card.Key); Assert.Equal("Owner card", card.Title); // older published boards work too
        Assert.Equal(imported.BoardId, card.BoardId);
        Assert.Equal(0, server.Publications); Assert.Equal(0, server.ActivityWrites);
        await store.UpdateCardAsync(root, card.Id, new(Title: "Member edit"), Ct);
        await store.AddCommentAsync(root, card.Id, BoardAuthor.User(), "Member discussion", Ct);
        await sync.SyncNowAsync(root, imported.BoardId, Ct);
        Assert.Contains(server.Events, e => e.Changes is {} changes && changes.TryGetProperty("title", out var title) && title.GetProperty("to").GetString() == "Member edit");
        Assert.Contains(server.Events, e => e.Body == "Member discussion");
        server.Event("change", """{"description":{"to":"Website edit"}}""");
        await sync.SyncNowAsync(root, imported.BoardId, Ct);
        Assert.Equal("Website edit", (await store.FindCardAsync(root, card.Id, Ct))!.Description);
        Assert.Equal(0, (await sync.GetStatusAsync(root, imported.BoardId, Ct))!.Unsent);
        Assert.Equal(imported.BoardId, (await sharing.ImportAsync(root, server.Remote.RemoteBoardId, Ct)).BoardId);
        Assert.Single(await store.GetBoardsAsync(root, Ct));
        Assert.Equal(0, server.Publications);
    }

    [Fact]
    public async Task RevocationNeverRepublishesOrDeletesRetainedLocalWork()
    {
        var board = await sharing.ImportAsync(root, server.Remote.RemoteBoardId, Ct);
        await store.UpdateCardAsync(root, "card_000000000001", new(Title: "Unsent local work"), Ct);
        server.Revoked = true;
        var result = (await sync.SyncNowAsync(root, board.BoardId, Ct))!;
        Assert.NotNull(result.LastError); Assert.True(result.Unsent > 0);
        Assert.Equal("Unsent local work", (await store.FindCardAsync(root, "card_000000000001", Ct))!.Title);
        Assert.Equal(0, server.Publications);
        await sync.SyncDueAsync(Ct);
        Assert.Equal(0, server.Publications);
    }

    [Fact]
    public async Task AccountChangeCannotRebindOrSendImportedContent()
    {
        var board = await sharing.ImportAsync(root, server.Remote.RemoteBoardId, Ct);
        var calls = server.Calls;
        server.DestinationKey = "another-account";
        await sync.SyncDueAsync(Ct);
        Assert.Equal(calls, server.Calls);
        Assert.Equal("member-account", (await store.GetSyncLinkAsync(root, board.BoardId, Ct))!.DestinationKey);
        await Assert.ThrowsAsync<BoardValidationException>(() => sharing.SaveAsync(root, board.BoardId, null, new("someone@example.test"), Ct));
        await Assert.ThrowsAsync<BoardConflictException>(() => sharing.ImportAsync(root, server.Remote.RemoteBoardId, Ct));
        Assert.Equal(0, server.Publications);
    }

    [Fact]
    public async Task SharedOriginsSurviveLegacyColumnWritesAndProjectScopeIsEnforced()
    {
        var board = await sharing.ImportAsync(root, server.Remote.RemoteBoardId, Ct);
        await using (var db = new SqliteConnection(cs))
        {
            await db.OpenAsync(Ct); await using var command = db.CreateCommand();
            command.CommandText = "UPDATE BoardSyncLinks SET DestinationKey='wrong', RemoteBoardId='wrong', Enabled=0";
            await command.ExecuteNonQueryAsync(Ct);
        }
        var reopened = new BoardStore(cs, cs);
        var link = (await reopened.GetSyncLinkAsync(root, board.BoardId, Ct))!;
        Assert.True(link.Imported); Assert.Equal("member-account", link.DestinationKey);
        Assert.Equal(server.Remote.RemoteBoardId, link.RemoteBoardId);
        Assert.Null(await reopened.GetSyncLinkAsync(root + "-other", board.BoardId, Ct));
        await Assert.ThrowsAsync<BoardConflictException>(() => sharing.ImportAsync(root + "-other", server.Remote.RemoteBoardId, Ct));
    }

    [Fact]
    public async Task BothSidesCanChangeLanesAndConcurrentLocalLayoutIsPreserved()
    {
        var result = await sharing.ImportAsync(root, server.Remote.RemoteBoardId, Ct);
        server.Remote = server.Remote with { Name = "Renamed remotely", Lanes = [server.Remote.Lanes[0], new("col_done", "Done", null, 1)] };
        server.Event("change", """{"lane":{"to":"col_done"}}""");
        await sync.SyncNowAsync(root, result.BoardId, Ct);
        Assert.Equal("col_done", (await store.FindCardAsync(root, "card_000000000001", Ct))!.ColumnId);
        Assert.Equal("Renamed remotely", (await store.GetBoardAsync(root, result.BoardId, Ct))!.Name);
        var before = (await store.GetBoardAsync(root, result.BoardId, Ct))!;
        var columns = await store.GetColumnsAsync(root, Ct, result.BoardId);
        await store.RenameBoardAsync(root, result.BoardId, "Local draft", null, Ct);
        Assert.Null(await store.ApplyRemoteLayoutAsync(root, result.BoardId, server.Remote, before, columns, Ct));
        await sync.SyncNowAsync(root, result.BoardId, Ct);
        Assert.Equal("Local draft", server.Remote.Name);
        Assert.Equal("VB", server.Remote.KeyPrefix); // never sends this checkout's unrelated project prefix
        server.Remote = server.Remote with { Lanes = [new("col_done", "Done", "#ffffff", 0)] };
        await sync.SyncNowAsync(root, result.BoardId, Ct);
        Assert.Equal("col_done", Assert.Single(await store.GetColumnsAsync(root, Ct, result.BoardId)).Id);
    }

    [Fact]
    public async Task TransferRestoreOnSharedBoardReappearsAndOlderRestoreCannotUndoLaterDeletion()
    {
        var board = await sharing.ImportAsync(root, server.Remote.RemoteBoardId, Ct);
        server.Event("deleted", "{}"); server.Event("restored", "{}");
        await sync.SyncNowAsync(root, board.BoardId, Ct);
        Assert.NotNull(await store.FindCardAsync(root, "card_000000000001", Ct));
        await store.DeleteCardAsync(root, "card_000000000001", Ct);
        await sync.SyncNowAsync(root, board.BoardId, Ct);
        await store.RestoreSharedCardAsync(root, "card_000000000001", BoardAuthor.System(),
            new("old_restore", 3, DateTime.UtcNow, BoardId: board.BoardId), Ct);
        Assert.Null(await store.FindCardAsync(root, "card_000000000001", Ct));
    }

    [Fact]
    public async Task InvalidOrCollidingRemoteLanesRollBackTheWholeImport()
    {
        await sharing.ImportAsync(root, server.Remote.RemoteBoardId, Ct);
        var original = (await store.GetBoardsAsync(root, Ct)).Count;
        var another = server.Remote with { RemoteBoardId = Guid.NewGuid().ToString("D") };
        await Assert.ThrowsAsync<BoardConflictException>(() => store.ImportSharedBoardAsync(root, another, server.DestinationKey!, Ct));
        await Assert.ThrowsAsync<BoardValidationException>(() => store.ImportSharedBoardAsync(root,
            another with { Lanes = [new("../outside", "Bad", null, 0)] }, server.DestinationKey!, Ct));
        Assert.Equal(original, (await store.GetBoardsAsync(root, Ct)).Count);
    }

    private sealed class SharedServer : IBoardSyncClient
    {
        public bool IsConfigured => true;
        public string? DestinationKey { get; set; } = "member-account";
        public BoardRemoteDescriptor Remote = new(Guid.NewGuid().ToString("D"), "Shared", "VB", "VB", [new("col_shared", "Todo", null, 0)], false, 0);
        public List<BoardSyncPulledEntryWire> Events = [];
        public int Publications, ActivityWrites, Calls;
        public bool Revoked;
        private void Check(string? destination)
        { Calls++; Assert.Equal(DestinationKey, destination); if (Revoked) throw new BoardSyncClientException("Access no longer available", "board_not_found", 404); }
        public Task<BoardRemoteDescriptor?> DescribeAsync(string id, CancellationToken ct, string? destination = null)
        { Check(destination); Assert.Equal(Remote.RemoteBoardId, id); return Task.FromResult<BoardRemoteDescriptor?>(Remote); }
        public Task<List<BoardRemoteDescriptor>> DiscoverAsync(CancellationToken ct, string? destination = null)
        { Check(destination); return Task.FromResult(new List<BoardRemoteDescriptor> { Remote }); }
        public Task<BoardSyncPublishResponse> PublishAsync(BoardSyncPublishRequest body, CancellationToken ct, string? expectedDestination = null)
        { Publications++; throw new InvalidOperationException("Imported boards must never publish"); }
        public Task<BoardSyncActivityAck> PutActivityAsync(string board, string card, BoardSyncActivityWire body, CancellationToken ct, string? expectedDestination = null)
        { ActivityWrites++; throw new InvalidOperationException("Member snapshots must not replace owner activity"); }
        public Task<BoardSyncPullResponse> PullAsync(string board, long after, int limit, CancellationToken ct, string? expectedDestination = null)
        { Check(expectedDestination); return Task.FromResult(new BoardSyncPullResponse(Events.Where(e => e.Seq > after).Take(limit).ToList(), Events.Count, Events.Count > after + limit)); }
        public Task<BoardSyncPushResponse> PushAsync(string board, BoardSyncPushRequest body, CancellationToken ct, string? expectedDestination = null)
        {
            Check(expectedDestination);
            if (body.Name is not null) Remote = Remote with { Name = body.Name };
            if (body.KeyPrefix is not null) Remote = Remote with { KeyPrefix = body.KeyPrefix };
            if (body.Lanes is not null) Remote = Remote with { Lanes = body.Lanes.Select(l => new BoardRemoteLane(l.Id, l.Name, l.Color, l.Position)).ToList() };
            var accepted = new List<BoardSyncAcceptedWire>();
            foreach (var e in body.Entries)
            {
                var stored = Events.FirstOrDefault(x => x.Id == e.Id);
                if (stored is null) { stored = new(e.Id, e.CardId, e.CardKey, e.Kind, e.Author, e.Body, e.Changes, e.CreatedUtc, Events.Count + 1, "desktop"); Events.Add(stored); }
                accepted.Add(new(e.Id, stored.Seq));
            }
            return Task.FromResult(new BoardSyncPushResponse(accepted, Events.Count));
        }
        public void Event(string kind, string changes) => Events.Add(new("log_" + Guid.NewGuid().ToString("N"), "card_000000000001", "VB-1", kind,
            new("user", "Owner", null), "", JsonDocument.Parse(changes).RootElement.Clone(), DateTime.UtcNow, Events.Count + 1, "web"));
    }
    public void Dispose() { cache.Dispose(); Directory.Delete(root, true); }
}
