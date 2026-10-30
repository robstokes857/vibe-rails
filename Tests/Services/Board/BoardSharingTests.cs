using System.Text.Json;
using Microsoft.Data.Sqlite;
using VibeRails.DTOs;
using VibeRails.Services.Board;
using VibeRails.Services.Board.Sync;
using VibeRails.Services.Diagnostics;
using Xunit;

namespace Tests.Services.Board;

/// <summary>
/// VIBE-235: the owner's local board is the source of truth and collaborators read shared boards
/// on viberails.ai. A copy an older version imported from another account's share (VB-52) is kept
/// as it is but never pushes, pulls or publishes activity. Owned boards still sync both ways.
/// </summary>
public sealed class BoardSharingTests : IDisposable
{
    private const string SharedCard = "card_000000000001";
    private readonly string root = Path.Combine(Path.GetTempPath(), "board-sharing-" + Guid.NewGuid().ToString("N"));
    private readonly BoardStore store;
    private readonly HostedServer server = new();
    private readonly BoardSyncActivityCache cache = new();
    private readonly BoardSyncService sync;
    private readonly string cs;
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public BoardSharingTests()
    {
        Directory.CreateDirectory(root); cs = $"Data Source={Path.Combine(root, "board.db")};Pooling=False";
        store = new(cs, cs);
        sync = new(store, server, new BoardSyncLock(Path.Combine(root, "sync.lock")), NullFeatureLog.Instance, cache);
    }

    /// <summary>An import as an older version left it: the owner's card pulled, then member work queued on top.</summary>
    private async Task<BoardRecord> RetainedImportAsync()
    {
        var board = await store.ImportSharedBoardAsync(root, server.Shared, server.DestinationKey!, Ct);
        Assert.NotNull(await store.CreateSyncedCardAsync(root, SharedCard, "VB-QWERT-1",
            new("col_shared", "Owner card", "Shared text", null, "medium", null, [], false, BoardId: board.Id),
            BoardAuthor.User(), new("log_owner_created", 1, DateTime.UtcNow, BoardId: board.Id), Ct));
        // Simulate the rows an older writable release left behind. Current store methods must
        // refuse these edits, while the upgrade must keep the old data exactly as it was.
        await using (var db = new SqliteConnection(cs))
        {
            await db.OpenAsync(Ct);
            await using var command = db.CreateCommand();
            command.CommandText = """
                UPDATE BoardCards SET Title='Member edit' WHERE Id=$card;
                INSERT INTO BoardComments (Id,CardId,AuthorKind,AuthorLabel,Body,CreatedUTC,Kind)
                VALUES ('legacy-comment',$card,'user','You','Member discussion',$now,'comment');
                INSERT INTO BoardCardSessions (SessionId,CardId,Selection,Cli,DisplayName,Origin,CreatedUTC)
                VALUES ($session,$card,'base:codex','codex','Member work','mcp',$now);
                """;
            command.Parameters.AddWithValue("$card", SharedCard);
            command.Parameters.AddWithValue("$session", Guid.NewGuid().ToString("D"));
            command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync(Ct);
        }
        // The owner has moved on since: a web change waits on the hosted copy.
        server.Event(server.Shared.RemoteBoardId, SharedCard, "VB-QWERT-1", "change", """{"description":{"to":"Website edit"}}""");
        return board;
    }

    [Fact]
    public async Task ImportedCopyIsSkippedByScheduledAndManualSyncAndKeepsItsLocalData()
    {
        var board = await RetainedImportAsync();
        Assert.True((await store.GetBoardAsync(root, board.Id, Ct))!.SyncEnabled); // an older import stays opted in
        var linkBefore = (await store.GetSyncLinkAsync(root, board.Id, Ct))!;
        var unsentBefore = await store.CountUnsentLogEntriesAsync(board.Id, Ct);
        Assert.True(unsentBefore > 0);

        await sync.SyncDueAsync(Ct);
        var manual = await Assert.ThrowsAsync<BoardValidationException>(() => sync.SyncNowAsync(root, board.Id, Ct));
        Assert.Equal(BoardSyncService.SharedBoardReadOnlyMessage, manual.Message);
        var enable = await Assert.ThrowsAsync<BoardValidationException>(() => sync.SetPublishedAsync(root, board.Id, true, Ct));
        Assert.Equal(BoardSyncService.SharedBoardReadOnlyMessage, enable.Message);
        // A different signed-in account changes nothing either: there is no sync to rebind.
        server.DestinationKey = "another-account";
        await sync.SyncDueAsync(Ct);

        Assert.Empty(server.Calls);
        Assert.Equal(0, server.Publications);
        var card = (await store.FindCardAsync(root, SharedCard, Ct))!;
        Assert.Equal(("Member edit", "Shared text", board.Id), (card.Title, card.Description, card.BoardId));
        var detail = (await store.GetCardDetailAsync(root, SharedCard, Ct))!;
        Assert.Contains(detail.Comments, comment => comment.Body == "Member discussion");
        Assert.Single(detail.Sessions);
        Assert.Equal(unsentBefore, await store.CountUnsentLogEntriesAsync(board.Id, Ct)); // retained, never sent or dropped
        var link = (await store.GetSyncLinkAsync(root, board.Id, Ct))!;
        Assert.Equal((linkBefore.RemoteBoardId, linkBefore.DestinationKey, linkBefore.Cursor, linkBefore.LastSyncUtc, linkBefore.LastError, true),
            (link.RemoteBoardId, link.DestinationKey, link.Cursor, link.LastSyncUtc, link.LastError, link.Imported));

        var status = (await sync.GetStatusAsync(root, board.Id, Ct))!;
        Assert.True(status.Shared);
        Assert.False(status.ActivityEnabled);
        Assert.Equal(server.Shared.RemoteBoardId, status.RemoteBoardId);
    }

    [Fact]
    public async Task RetainedSharedBoardRejectsLocalWritesWithoutChangingDataOrQueuingLogs()
    {
        var shared = await RetainedImportAsync();
        var own = await store.CreateBoardAsync(root, "Mine", Ct);
        var ownLane = await store.CreateColumnAsync(root, "Todo", "", Ct, own.Id);
        var ownCard = await store.CreateCardAsync(root, new(ownLane.Id, "Mine", "", null, "medium", null, [], false), Ct);
        var before = (await store.GetCardDetailAsync(root, SharedCard, Ct))!;
        var logs = await store.CountUnsentLogEntriesAsync(shared.Id, Ct);
        var session = Assert.Single(before.Sessions).SessionId;
        var actions = new Func<Task>[]
        {
            () => store.UpdateCardAsync(root, SharedCard, new(Title: "Forbidden"), Ct),
            () => store.AddCommentAsync(root, SharedCard, BoardAuthor.User(), "Forbidden", Ct),
            () => store.AddNoteAsync(root, SharedCard, BoardAuthor.User(), "Forbidden", Ct),
            () => store.DeleteCommentAsync(root, SharedCard, "legacy-comment", BoardAuthor.User(), Ct),
            () => store.DeleteCardAsync(root, SharedCard, Ct),
            () => store.MoveCardAsync(root, SharedCard, "col_shared", 0, Ct),
            () => store.MoveCardAsync(root, ownCard.Id, "col_shared", 0, Ct),
            () => store.CreateCardAsync(root, new("col_shared", "Forbidden", "", null, "medium", null, [], false), Ct),
            () => store.CreateColumnAsync(root, "Forbidden", "", Ct, shared.Id),
            () => store.UpdateColumnAsync(root, "col_shared", "Forbidden", null, Ct),
            () => store.DeleteColumnAsync(root, "col_shared", Ct),
            () => store.ReorderColumnsAsync(root, ["col_shared"], Ct, shared.Id),
            () => store.RenameBoardAsync(root, shared.Id, "Forbidden", null, Ct),
            () => store.DeleteBoardAsync(root, shared.Id, Ct),
            () => store.SetCardPinnedAsync(root, SharedCard, true, Ct),
            () => store.LinkCardAsync(root, ownCard.Id, SharedCard, Ct),
            () => store.LinkCardAsync(root, SharedCard, ownCard.Id, Ct),
            () => store.AddAttachmentContentAsync(root, SharedCard, "file.txt", "text/plain", [1], Ct),
            () => store.AddCodeLinkAsync(root, SharedCard, "https://github.com/example/repo/pull/1", Ct),
            () => store.LinkSessionAsync(root, SharedCard, Guid.NewGuid().ToString("D"), null, "base:codex", "codex", "Forbidden", "mcp", Ct),
            () => store.RenameSessionAsync(root, SharedCard, session, "Forbidden", Ct),
            () => store.UnlinkSessionAsync(root, SharedCard, session, Ct),
            () => store.CompleteAgentAsync(root, SharedCard, session, "completed", "Forbidden", Ct),
            () => store.StartDesktopActivityAsync(root, SharedCard, "client", "Agent", DateTime.UtcNow.AddMinutes(1), Ct),
            () => store.SaveReviewSettingsAsync(root, SharedCard, new BoardReviewSettings(), Ct),
            () => store.SetBoardSyncEnabledAsync(root, shared.Id, false, Ct),
            () => store.DeleteAttachmentAsync(root, SharedCard, "missing", Ct),
            () => store.SaveEveryContextSettingsAsync(root, shared.Id, "Forbidden", 0, Ct),
            () => store.SaveContextSettingsAsync(root, shared.Id, BoardContextSettings.Empty, 0, Ct),
            () => store.SaveLaneAutomationAsync(root, "col_shared", [], 0, Ct)
        };
        foreach (var action in actions)
            Assert.Equal(BoardAccess.ReadOnlyMessage, (await Assert.ThrowsAsync<BoardValidationException>(action)).Message);

        Assert.Equivalent(before.Card, (await store.GetCardDetailAsync(root, SharedCard, Ct))!.Card, strict: true);
        Assert.Equal(logs, await store.CountUnsentLogEntriesAsync(shared.Id, Ct));
        Assert.Equal("Member discussion", Assert.Single((await store.GetCardDetailAsync(root, SharedCard, Ct))!.Comments).Body);
        Assert.Equal("Member work", Assert.Single((await store.GetCardDetailAsync(root, SharedCard, Ct))!.Sessions).DisplayName);
        Assert.Empty(await store.GetLinkedCardIdsAsync(root, ownCard.Id, Ct));
    }

    [Fact]
    public async Task OldSharedLaneEntriesStayStoredButNeverDispatchOrStarveOwnedCards()
    {
        await RetainedImportAsync();
        var own = await store.CreateBoardAsync(root, "Mine", Ct);
        var lane = await store.CreateColumnAsync(root, "Todo", "", Ct, own.Id);
        var card = await store.CreateCardAsync(root, new(lane.Id, "Mine", "", null, "medium", null, [], false), Ct);
        await using (var db = new SqliteConnection(cs))
        {
            await db.OpenAsync(Ct);
            await using var command = db.CreateCommand();
            command.CommandText = """
                INSERT INTO BoardLaneAutomations(ColumnId,JobId,Revision) VALUES ('col_shared',77,1),($lane,77,1);
                INSERT INTO BoardPendingAutomations(CardId,ColumnId,JobId,EventKey,DueUnixMs)
                VALUES ($shared,'col_shared',77,'old-shared-event',0),($own,$lane,77,'owned-event',1);
                """;
            command.Parameters.AddWithValue("$shared", SharedCard);
            command.Parameters.AddWithValue("$own", card.Id);
            command.Parameters.AddWithValue("$lane", lane.Id);
            await command.ExecuteNonQueryAsync(Ct);
        }
        var due = Assert.Single(await store.GetDueLaneAutomationsAsync(DateTime.UtcNow, Ct));
        Assert.Equal(card.Id, due.CardId);
        Assert.True(await store.IsLaneAutomationCurrentAsync(due, Ct));
        Assert.False(await store.IsLaneAutomationCurrentAsync(new(SharedCard, 77, "old-shared-event", root,
            "board-lane:VB-QWERT-1:col_shared:old-shared-event", true), Ct));
        Assert.Single(await store.GetPendingLaneAutomationsAsync(root, SharedCard, Ct));
    }

    [Fact]
    public async Task OwnedBoardsStillPushPullAndPublishActivityBesideAnImportedCopy()
    {
        await RetainedImportAsync();
        var own = await store.CreateBoardAsync(root, "Mine", Ct);
        await store.CreateColumnAsync(root, "Todo", "", Ct, own.Id);
        var card = await store.CreateCardAsync(root, new(null, "Owner work", "", null, "medium", null, [], false, BoardId: own.Id), Ct);
        await store.LinkSessionAsync(root, card.Id, Guid.NewGuid().ToString("D"), null, "base:claude", "claude", "Owner session",
            BoardSessionRecord.McpOrigin, Ct);

        // Publishing pushes the owner's card and publishes its activity.
        var published = (await sync.SetPublishedAsync(root, own.Id, true, Ct))!;
        Assert.Null(published.LastError);
        Assert.False(published.Shared);
        Assert.True(published.ActivityEnabled);
        var remote = published.RemoteBoardId!;
        Assert.Equal(1, server.Publications);
        Assert.Contains(server.Entries[remote], e => e.CardId == card.Id && e.Kind == "created");
        Assert.Contains(server.ActivityCards, write => write == (remote, card.Id));

        // A web change on the owned board is pulled, and a local edit is pushed, on the scheduled tick.
        server.Event(remote, card.Id, card.Key, "change", """{"description":{"to":"Web note"}}""");
        await store.AddCommentAsync(root, card.Id, BoardAuthor.User(), "Owner discussion", Ct);
        await sync.SyncDueAsync(Ct);
        Assert.Equal("Web note", (await store.FindCardAsync(root, card.Id, Ct))!.Description);
        Assert.Contains(server.Entries[remote], e => e.CardId == card.Id && e.Body == "Owner discussion");
        Assert.Equal(0, await store.CountUnsentLogEntriesAsync(own.Id, Ct));

        // Every call was for the owned board; the imported copy was never contacted.
        Assert.NotEmpty(server.Calls);
        Assert.All(server.Calls, call => Assert.Equal(remote, call.Board));
        Assert.Equal("Shared text", (await store.FindCardAsync(root, SharedCard, Ct))!.Description);
    }

    [Fact]
    public async Task SharedOriginsSurviveLegacyColumnWritesAndStayProjectScoped()
    {
        var board = await RetainedImportAsync();
        await using (var db = new SqliteConnection(cs))
        {
            await db.OpenAsync(Ct); await using var command = db.CreateCommand();
            command.CommandText = "UPDATE BoardSyncLinks SET DestinationKey='wrong', RemoteBoardId='wrong', Enabled=0";
            await command.ExecuteNonQueryAsync(Ct);
        }
        var reopened = new BoardStore(cs, cs);
        var link = (await reopened.GetSyncLinkAsync(root, board.Id, Ct))!;
        Assert.True(link.Imported); Assert.Equal("member-account", link.DestinationKey);
        Assert.Equal(server.Shared.RemoteBoardId, link.RemoteBoardId);
        Assert.Null(await reopened.GetSyncLinkAsync(root + "-other", board.Id, Ct));

        // The board list marks only this project's imported copies.
        await reopened.CreateBoardAsync(root, "Mine", Ct);
        var origins = await reopened.GetSharedBoardOriginsAsync(root, Ct);
        Assert.Equal(server.Shared.RemoteBoardId, Assert.Single(origins, o => o.Key == board.Id).Value);
        Assert.Single(origins);
        Assert.Empty(await reopened.GetSharedBoardOriginsAsync(root + "-other", Ct));
    }

    /// <summary>One hosted service holding the shared board and any board this account publishes.</summary>
    private sealed class HostedServer : IBoardSyncClient
    {
        public bool IsConfigured => true;
        public string? DestinationKey { get; set; } = "member-account";
        public readonly BoardRemoteDescriptor Shared = new(Guid.NewGuid().ToString("D"), "Shared", "VB", "VB", [new("col_shared", "Todo", null, 0)], false, 0);
        public readonly Dictionary<string, List<BoardSyncPulledEntryWire>> Entries = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<(string Method, string Board)> Calls = [];
        public readonly List<(string Board, string Card)> ActivityCards = [];
        public int Publications;

        private List<BoardSyncPulledEntryWire> Board(string board) =>
            Entries.TryGetValue(board, out var list) ? list : Entries[board] = [];
        private void Call(string method, string board, string? destination)
        {
            Assert.Equal(DestinationKey, destination);
            Calls.Add((method, board));
        }

        public Task<BoardSyncPublishResponse> PublishAsync(BoardSyncPublishRequest body, CancellationToken ct, string? expectedDestination = null)
        {
            Assert.Equal(DestinationKey, expectedDestination);
            Publications++;
            var id = Guid.NewGuid();
            Board(id.ToString("D"));
            return Task.FromResult(new BoardSyncPublishResponse(id, body.Name, 0));
        }
        public Task<BoardRemoteDescriptor?> DescribeAsync(string id, CancellationToken ct, string? destination = null)
        { Call("describe", id, destination); return Task.FromResult<BoardRemoteDescriptor?>(null); }
        public Task<BoardSyncActivityAck> PutActivityAsync(string board, string card, BoardSyncActivityWire body, CancellationToken ct, string? expectedDestination = null)
        { Call("activity", board, expectedDestination); ActivityCards.Add((board, card)); return Task.FromResult(new BoardSyncActivityAck(body.Schema, card)); }
        public Task PutContentPartAsync(BoardSyncContentPartWire part, ReadOnlyMemory<byte> bytes, CancellationToken ct, string? destination = null)
            => Task.CompletedTask;
        public Task<BoardSyncPullResponse> PullAsync(string board, long after, int limit, CancellationToken ct, string? expectedDestination = null)
        {
            Call("pull", board, expectedDestination);
            var events = Board(board);
            return Task.FromResult(new BoardSyncPullResponse(events.Where(e => e.Seq > after).Take(limit).ToList(), events.Count, events.Count > after + limit));
        }
        public Task<BoardSyncPushResponse> PushAsync(string board, BoardSyncPushRequest body, CancellationToken ct, string? expectedDestination = null)
        {
            Call("push", board, expectedDestination);
            var events = Board(board);
            var accepted = new List<BoardSyncAcceptedWire>();
            foreach (var e in body.Entries)
            {
                var stored = events.FirstOrDefault(x => x.Id == e.Id);
                if (stored is null) { stored = new(e.Id, e.CardId, e.CardKey, e.Kind, e.Author, e.Body, e.Changes, e.CreatedUtc, events.Count + 1, "desktop"); events.Add(stored); }
                accepted.Add(new(e.Id, stored.Seq));
            }
            return Task.FromResult(new BoardSyncPushResponse(accepted, events.Count));
        }
        public void Event(string board, string cardId, string cardKey, string kind, string changes)
        {
            var events = Board(board);
            events.Add(new("log_" + Guid.NewGuid().ToString("N"), cardId, cardKey, kind, new("user", "Owner", null), "",
                JsonDocument.Parse(changes).RootElement.Clone(), DateTime.UtcNow, events.Count + 1, "web"));
        }
    }

    public void Dispose() { cache.Dispose(); try { Directory.Delete(root, true); } catch (IOException) { } }
}
