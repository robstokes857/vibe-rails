using System.Text.Json;
using Microsoft.Data.Sqlite;
using VibeRails.Services.Board;
using VibeRails.Services.Board.Sync;
using VibeRails.Services.Diagnostics;
using Xunit;

namespace Tests.Services.Board;

/// <summary>
/// VIBE-85 (VB-4HTU2-157): the regressions for the VIBE-14 proofs. An imported shared board is
/// never republished by an older binary, never pushes a label correction caused by this machine's
/// own cards, never renames this machine's cards, and never lets a card cross the sharing
/// boundary by move or merge.
/// </summary>
public sealed class BoardSharingSafetyTests : IDisposable
{
    private const string SharedCard = "card_000000000001";
    private const string SecondSharedCard = "card_000000000002";
    private readonly string root = Path.Combine(Path.GetTempPath(), "board-sharing-safety-" + Guid.NewGuid().ToString("N"));
    private readonly BoardStore store;
    private readonly SharedServer server = new();
    private readonly BoardSyncActivityCache cache = new();
    private readonly BoardSyncService sync;
    private readonly BoardSharingService sharing;
    private readonly string cs;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public BoardSharingSafetyTests()
    {
        Directory.CreateDirectory(root);
        cs = $"Data Source={Path.Combine(root, "board.db")};Pooling=False";
        store = new(cs, cs);
        var gate = new BoardSyncLock(Path.Combine(root, "sync.lock"));
        sync = new(store, server, gate, NullFeatureLog.Instance, cache);
        sharing = new(store, server, sync, gate);
    }

    private static NewBoardCard Card(string title) => new(null, title, "", null, "medium", null, [], false);

    [Fact]
    public async Task ImportedLinkSatisfiesTheOlderBinaryPublishPredicate_AndAnEarlierImportIsBroughtUpToIt()
    {
        server.Event("created", """{"title":{"to":"Owner card"},"lane":{"to":"col_shared"}}""", "VB-QWERT-1");
        var imported = await sharing.ImportAsync(root, server.Remote.RemoteBoardId, Ct);
        var link = await ReadLinkRowAsync(imported.BoardId);
        Assert.Equal((server.Remote.RemoteBoardId, 1, BoardStore.ImportedLinkActivitySchema, "member-account"), link);
        Assert.True(OlderBinaryTreatsAsPublished(link));
        Assert.Equal(0, server.Publications);

        // An import written before VIBE-85 stored ActivitySchema 0 and failed that predicate (F1).
        await Sql("UPDATE BoardSyncLinks SET ActivitySchema = 0");
        Assert.False(OlderBinaryTreatsAsPublished(await ReadLinkRowAsync(imported.BoardId)));
        await sync.SyncDueAsync(Ct);
        link = await ReadLinkRowAsync(imported.BoardId);
        Assert.True(OlderBinaryTreatsAsPublished(link));
        Assert.Equal(server.Remote.RemoteBoardId, link.RemoteBoardId);
        Assert.Equal(0, server.Publications);
        Assert.Null((await sync.GetStatusAsync(root, imported.BoardId, Ct))!.LastError);
    }

    /// <summary>
    /// v10.11.4's <c>EnsurePublishedAsync</c> early return (VIBE-13 automatic publication, written
    /// before VB-52 imports existed): anything else it publishes as this account's own board.
    /// </summary>
    private bool OlderBinaryTreatsAsPublished((string RemoteBoardId, int Enabled, int ActivitySchema, string DestinationKey) link)
        => link is { Enabled: 1, ActivitySchema: >= 1 } && link.DestinationKey == server.DestinationKey;

    [Fact]
    public async Task IncomingLabelOnThisMachinesReservedKeyStaysLocal_NothingIsPushedAndNoCardIsRenamed()
    {
        await store.EnsureDefaultColumnsAsync(root, Ct);
        var local = await store.CreateCardAsync(root, Card("Member's own card"), Ct);
        var parts = local.Key.Split('-');
        var keyPrefix = parts[0];
        var reserved = keyPrefix + "-" + parts[^1]; // this machine's reserved short key, e.g. VPE-1 for VPE-D6BFU-1
        server.Remote = server.Remote with { KeyPrefix = keyPrefix, DisplayPrefix = keyPrefix };
        server.Event("created", $$$"""{"title":{"to":"Owner card"},"lane":{"to":"col_shared"},"displayId":{"to":"{{{reserved}}}"}}""",
            keyPrefix + "-QWERT-" + parts[^1]);

        var imported = await sharing.ImportAsync(root, server.Remote.RemoteBoardId, Ct);
        await sync.SyncNowAsync(root, imported.BoardId, Ct);

        var card = (await store.FindCardAsync(root, SharedCard, Ct))!;
        Assert.NotEqual(reserved, card.DisplayId);
        Assert.StartsWith(keyPrefix + "-", card.DisplayId, StringComparison.Ordinal);
        Assert.Empty(server.PushedDisplayIds(SharedCard)); // the proof observed the local label pushed here (F2)
        Assert.Equal(local.DisplayId, (await store.FindCardAsync(root, local.Id, Ct))!.DisplayId);
        Assert.Equal(0, await store.CountUnsentLogEntriesAsync(imported.BoardId, Ct));
        Assert.Equal(0, server.Publications);

        // The explanation is local History only: RemoteSeq 0, never queued, protecting no field.
        var note = Assert.Single(await store.GetCardHistoryAsync(root, card.Id, Ct),
            entry => entry.Kind == "change" && entry.Changes?.Contains("displayId", StringComparison.Ordinal) == true);
        Assert.Contains($"{reserved} is shown as {card.DisplayId}", note.Body, StringComparison.Ordinal);
        Assert.Equal(0L, await ScalarAsync($"SELECT RemoteSeq FROM BoardComments WHERE Id = '{note.Id}'"));
        server.Event("change", """{"displayId":{"to":"VPE-9"},"title":{"to":"Relabelled on the web"}}""", keyPrefix + "-QWERT-" + parts[^1]);
        await sync.SyncNowAsync(root, imported.BoardId, Ct);
        Assert.Equal("Relabelled on the web", (await store.FindCardAsync(root, SharedCard, Ct))!.Title);
    }

    [Fact]
    public async Task IncomingLabelOnThisMachinesOwnLabelNeverRenamesThatCard()
    {
        await store.EnsureDefaultColumnsAsync(root, Ct);
        var own = Assert.Single(await store.GetBoardsAsync(root, Ct));
        await store.RenameBoardAsync(root, own.Id, own.Name, "MINE", Ct);
        var local = await store.CreateCardAsync(root, Card("Member's own card"), Ct);
        Assert.Equal("MINE-1", local.DisplayId);
        var keyPrefix = local.Key.Split('-')[0];
        server.Remote = server.Remote with { KeyPrefix = keyPrefix, DisplayPrefix = "MINE" };
        server.Event("created", """{"title":{"to":"Owner card"},"lane":{"to":"col_shared"},"displayId":{"to":"MINE-1"}}""", keyPrefix + "-QWERT-77");

        var imported = await sharing.ImportAsync(root, server.Remote.RemoteBoardId, Ct);
        await sync.SyncNowAsync(root, imported.BoardId, Ct);

        var after = (await store.FindCardAsync(root, local.Id, Ct))!;
        Assert.Equal("MINE-1", after.DisplayId); // the proof observed VIBE-1 → VIBE-2 here (F3)
        Assert.Equal(local.Id, (await store.FindCardAsync(root, "MINE-1", Ct))!.Id);
        var card = (await store.FindCardAsync(root, SharedCard, Ct))!;
        Assert.Equal("MINE-2", card.DisplayId);
        Assert.Empty(server.PushedDisplayIds(SharedCard));
        Assert.DoesNotContain(await store.GetUnsentLogEntriesAsync(own.Id, 20, Ct), entry => entry.Entry.Kind == "change");
        Assert.Equal(0, await store.CountUnsentLogEntriesAsync(imported.BoardId, Ct));
    }

    [Fact]
    public async Task AnOwnedBoardsIncomingLabelNeverRenamesAnImportedCard()
    {
        await store.EnsureDefaultColumnsAsync(root, Ct);
        var own = Assert.Single(await store.GetBoardsAsync(root, Ct));
        await store.RenameBoardAsync(root, own.Id, own.Name, "MINE", Ct);
        var keyPrefix = (await store.CreateCardAsync(root, Card("Seed"), Ct)).Key.Split('-')[0];
        server.Remote = server.Remote with { KeyPrefix = keyPrefix, DisplayPrefix = "MINE" };
        server.Event("created", """{"title":{"to":"Owner card"},"lane":{"to":"col_shared"},"displayId":{"to":"MINE-5"}}""", keyPrefix + "-QWERT-5");
        var imported = await sharing.ImportAsync(root, server.Remote.RemoteBoardId, Ct);
        Assert.Equal("MINE-5", (await store.FindCardAsync(root, SharedCard, Ct))!.DisplayId);

        // A web card of this machine's own published board arrives wearing the same label.
        var incoming = (await store.CreateSyncedCardAsync(root, "card_0000000000aa", keyPrefix + "-ZZZZZ-9",
            Card("Own web card") with { BoardId = own.Id, DisplayId = "MINE-5" }, BoardAuthor.User(),
            new("log_own_label", 1, DateTime.UtcNow, BoardId: own.Id), Ct))!;
        Assert.NotEqual("MINE-5", incoming.DisplayId);
        Assert.Equal("MINE-5", (await store.FindCardAsync(root, SharedCard, Ct))!.DisplayId);
        Assert.Equal(SharedCard, (await store.FindCardAsync(root, "MINE-5", Ct))!.Id);
        // The owned board corrects its own label on viberails.ai, as VB-69 always did for a reserved label.
        Assert.Contains(await store.GetUnsentLogEntriesAsync(own.Id, 20, Ct), entry => entry.Entry.CardId == incoming.Id
            && entry.Entry.Kind == "change" && entry.Entry.Changes!.Contains("displayId", StringComparison.Ordinal));
        Assert.Equal(0, await store.CountUnsentLogEntriesAsync(imported.BoardId, Ct));
    }

    [Fact]
    public async Task CardsNeverCrossTheSharingBoundaryByMoveOrMerge()
    {
        await store.EnsureDefaultColumnsAsync(root, Ct);
        var own = Assert.Single(await store.GetBoardsAsync(root, Ct));
        var ownLane = (await store.GetColumnsAsync(root, Ct, own.Id)).OrderBy(c => c.Position).First();
        var mine = await store.CreateCardAsync(root, Card("My card"), Ct);
        server.Remote = server.Remote with { Lanes = [new("col_shared", "Todo", null, 0), new("col_shared_done", "Done", null, 1)] };
        server.Event("created", """{"title":{"to":"Owner card"},"lane":{"to":"col_shared"}}""", "VB-QWERT-1");
        server.Event("created", """{"title":{"to":"Second owner card"},"lane":{"to":"col_shared"}}""", "VB-QWERT-2", SecondSharedCard);
        var imported = await sharing.ImportAsync(root, server.Remote.RemoteBoardId, Ct);
        var eventsBefore = server.Events.Count;
        var status = (await sync.GetStatusAsync(root, imported.BoardId, Ct))!;
        Assert.True(status.LastError is null && status.Skipped == 0, $"import: {status.LastError} skipped={status.Skipped} {string.Join(";", status.SkippedEntries!.Select(s => s.Reason))}");
        Assert.Equal(imported.BoardId, (await store.FindCardAsync(root, SharedCard, Ct))!.BoardId);
        Assert.NotEqual(own.Id, imported.BoardId);

        // Out of the imported board, by both write paths a move can take.
        var refused = await Assert.ThrowsAsync<BoardValidationException>(() => store.MoveCardAsync(root, SharedCard, ownLane.Id, null, Ct));
        Assert.Contains("Copy the card", refused.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<BoardValidationException>(() => store.UpdateCardAsync(root, SharedCard, new(ColumnId: ownLane.Id), Ct));
        // Into it.
        await Assert.ThrowsAsync<BoardValidationException>(() => store.MoveCardAsync(root, mine.Id, "col_shared", null, Ct));
        await Assert.ThrowsAsync<BoardValidationException>(() => store.UpdateCardAsync(root, mine.Id, new(ColumnId: "col_shared"), Ct));
        // Merges across the boundary and within the shared board.
        foreach (var (source, target) in new[] { (SharedCard, mine.Id), (mine.Id, SharedCard), (SharedCard, SecondSharedCard) })
            Assert.Contains("Copy the card", (await Assert.ThrowsAsync<BoardValidationException>(() => store.MergeCardsAsync(root, source, target, Ct))).Message, StringComparison.Ordinal);

        // Nothing moved, nothing was deleted, nothing was queued for either board.
        Assert.Equal("col_shared", (await store.FindCardAsync(root, SharedCard, Ct))!.ColumnId);
        Assert.NotNull(await store.FindCardAsync(root, SecondSharedCard, Ct));
        Assert.Equal(ownLane.Id, (await store.FindCardAsync(root, mine.Id, Ct))!.ColumnId);
        Assert.Equal(0, await store.CountUnsentLogEntriesAsync(imported.BoardId, Ct));
        await sync.SyncNowAsync(root, imported.BoardId, Ct);
        Assert.Equal(eventsBefore, server.Events.Count);
        Assert.Equal(0, server.Publications);

        // Lane to lane inside the shared board stays an ordinary member edit.
        await store.MoveCardAsync(root, SharedCard, "col_shared_done", null, Ct);
        await sync.SyncNowAsync(root, imported.BoardId, Ct);
        Assert.Contains(server.Events, e => e.CardId == SharedCard && e.Kind == "change" && e.Changes is { } changes
            && changes.TryGetProperty("lane", out var lane) && lane.GetProperty("to").GetString() == "col_shared_done");
        // And a move between this machine's own boards is unaffected.
        var second = await store.CreateBoardAsync(root, "Second", Ct);
        var secondLane = await store.CreateColumnAsync(root, "Todo", "", Ct, second.Id);
        Assert.Equal(secondLane.Id, (await store.MoveCardAsync(root, mine.Id, secondLane.Id, null, Ct))!.ColumnId);
    }

    private async Task<(string RemoteBoardId, int Enabled, int ActivitySchema, string DestinationKey)> ReadLinkRowAsync(string boardId)
    {
        await using var db = new SqliteConnection(cs);
        await db.OpenAsync(Ct);
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT RemoteBoardId, Enabled, ActivitySchema, DestinationKey FROM BoardSyncLinks WHERE BoardId = $board";
        command.Parameters.AddWithValue("$board", boardId);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        Assert.True(await reader.ReadAsync(Ct));
        return (reader.GetString(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetString(3));
    }

    private async Task Sql(string commandText)
    {
        await using var db = new SqliteConnection(cs);
        await db.OpenAsync(Ct);
        await using var command = db.CreateCommand();
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync(Ct);
    }

    private async Task<object?> ScalarAsync(string commandText)
    {
        await using var db = new SqliteConnection(cs);
        await db.OpenAsync(Ct);
        await using var command = db.CreateCommand();
        command.CommandText = commandText;
        return await command.ExecuteScalarAsync(Ct);
    }

    public void Dispose() { cache.Dispose(); try { Directory.Delete(root, true); } catch (IOException) { } }

    private sealed class SharedServer : IBoardSyncClient
    {
        public bool IsConfigured => true;
        public string? DestinationKey => "member-account";
        public BoardRemoteDescriptor Remote = new(Guid.NewGuid().ToString("D"), "Shared", "VB", "VB", [new("col_shared", "Todo", null, 0)], false, 0);
        public List<BoardSyncPulledEntryWire> Events = [];
        public List<BoardSyncEntryWire> Pushed = [];
        public int Publications;

        /// <summary>Every <c>displayId</c> value this desktop pushed for the card.</summary>
        public List<string?> PushedDisplayIds(string cardId) => Pushed
            .Where(e => e.CardId == cardId && e.Changes is { ValueKind: JsonValueKind.Object } changes && changes.TryGetProperty("displayId", out _))
            .Select(e => e.Changes!.Value.GetProperty("displayId").GetProperty("to").GetString()).ToList();

        public Task<BoardRemoteDescriptor?> DescribeAsync(string id, CancellationToken ct, string? destination = null)
        { Assert.Equal(DestinationKey, destination); Assert.Equal(Remote.RemoteBoardId, id); return Task.FromResult<BoardRemoteDescriptor?>(Remote); }
        public Task<List<BoardRemoteDescriptor>> DiscoverAsync(CancellationToken ct, string? destination = null)
            => Task.FromResult(new List<BoardRemoteDescriptor> { Remote });
        public Task<BoardSyncPublishResponse> PublishAsync(BoardSyncPublishRequest body, CancellationToken ct, string? expectedDestination = null)
        { Publications++; throw new InvalidOperationException("Imported boards must never publish"); }
        public Task<BoardSyncActivityAck> PutActivityAsync(string board, string card, BoardSyncActivityWire body, CancellationToken ct, string? expectedDestination = null)
            => Task.FromResult(new BoardSyncActivityAck(1, card));
        public Task<BoardSyncPullResponse> PullAsync(string board, long after, int limit, CancellationToken ct, string? expectedDestination = null)
            => Task.FromResult(new BoardSyncPullResponse(Events.Where(e => e.Seq > after).Take(limit).ToList(), Events.Count, Events.Count > after + limit));
        public Task<BoardSyncPushResponse> PushAsync(string board, BoardSyncPushRequest body, CancellationToken ct, string? expectedDestination = null)
        {
            Assert.Equal(DestinationKey, expectedDestination);
            if (body.Name is not null) Remote = Remote with { Name = body.Name };
            if (body.Lanes is not null) Remote = Remote with { Lanes = body.Lanes.Select(l => new BoardRemoteLane(l.Id, l.Name, l.Color, l.Position)).ToList() };
            var accepted = new List<BoardSyncAcceptedWire>();
            foreach (var e in body.Entries)
            {
                Pushed.Add(e);
                var stored = Events.FirstOrDefault(x => x.Id == e.Id);
                if (stored is null) { stored = new(e.Id, e.CardId, e.CardKey, e.Kind, e.Author, e.Body, e.Changes, e.CreatedUtc, Events.Count + 1, "desktop"); Events.Add(stored); }
                accepted.Add(new(e.Id, stored.Seq));
            }
            return Task.FromResult(new BoardSyncPushResponse(accepted, Events.Count));
        }
        public void Event(string kind, string changes, string key, string cardId = SharedCard) => Events.Add(new("log_" + Guid.NewGuid().ToString("N"), cardId, key, kind,
            new("user", "Owner", null), "", JsonDocument.Parse(changes).RootElement.Clone(), DateTime.UtcNow, Events.Count + 1, "web"));
    }
}
