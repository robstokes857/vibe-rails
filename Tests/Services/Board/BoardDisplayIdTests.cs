using Microsoft.Data.Sqlite;
using VibeRails.Services.Board;
using Xunit;

namespace Tests.Services.Board;

public sealed class BoardDisplayIdTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "board-display-" + Guid.NewGuid().ToString("N"));
    private readonly string cs;
    private readonly BoardStore store;
    private readonly string project;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public BoardDisplayIdTests()
    {
        Directory.CreateDirectory(root);
        project = Path.Combine(root, "vibe-rails");
        cs = $"Data Source={Path.Combine(root, "board.db")};Pooling=False";
        store = new BoardStore(cs, cs);
    }

    private static NewBoardCard Card(string title = "Work") => new(null, title, "", null, "medium", null, [], false);

    [Fact]
    public async Task LabelsAreSequentialEditableSearchableAndIndependentOfIdentity()
    {
        await store.EnsureDefaultColumnsAsync(project, Ct);
        var first = await store.CreateCardAsync(project, Card(), Ct);
        var next = await store.CreateCardAsync(project, Card(), Ct);
        Assert.Equal("VIBE-1", first.DisplayId);
        Assert.Equal("VIBE-2", next.DisplayId);
        Assert.True(BoardKeys.TryParseStored(first.Key, out _));
        Assert.Equal(first.Id, (await store.FindCardAsync(project, "vibe-1", Ct))!.Id);
        var renamed = (await store.UpdateCardAsync(project, first.Id, new(DisplayId: "ABC-42"), Ct))!;
        Assert.Equal(first.Key, renamed.Key);
        Assert.Equal(first.Id, (await store.FindCardAsync(project, first.Key, Ct))!.Id);
        Assert.Equal(first.Id, (await store.FindCardAsync(project, "abc-42", Ct))!.Id);
        Assert.Null(await store.FindCardAsync(project, "VIBE-1", Ct));
        var page = await store.GetCardsPageAsync(project, new(Q: "abc-42"), Ct);
        Assert.Equal(first.Id, Assert.Single(page.Cards).Id);
        Assert.Equal(first.Id, Assert.Single((await store.GetCardLinkCandidatesAsync(project, null, "abc-42", Ct))!).Id);
        await Assert.ThrowsAsync<BoardConflictException>(() => store.UpdateCardAsync(project, next.Id, new(DisplayId: "abc-42"), Ct));
    }

    [Fact]
    public async Task BoardPrefixAffectsFutureCardsAndNumberSurvivesDeletionAndParallelWriters()
    {
        await store.EnsureDefaultColumnsAsync(project, Ct);
        var first = await store.CreateCardAsync(project, Card(), Ct);
        var board = (await store.GetBoardsAsync(project, Ct))[0];
        await store.RenameBoardAsync(project, board.Id, board.Name, "vb", Ct);
        var custom = await store.CreateCardAsync(project, Card(), Ct);
        // Every project's keys answer to the VB alias: VB-1 is the first card's short key, so the
        // new label numbers from the card high-water mark and is the new card's own short key.
        Assert.Equal("VB-2", custom.DisplayId);
        Assert.Equal(first.Id, (await store.FindCardAsync(project, "VB-1", Ct))!.Id);
        Assert.Equal("VIBE-1", (await store.FindCardAsync(project, first.Id, Ct))!.DisplayId);
        await store.DeleteCardAsync(project, custom.Id, Ct);
        var token = Ct;
        var cards = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => store.CreateCardAsync(project, Card(), token), token)));
        Assert.Equal(8, cards.Select(c => c.DisplayId).Distinct().Count());
        Assert.DoesNotContain(cards, c => c.DisplayId is "VB-1" or "VB-2");
        Assert.All(cards, c => Assert.Equal(BoardKeys.Format("VB", c.Number), c.DisplayId));
    }

    [Fact]
    public async Task LabelsNeverSpellAnotherCardsShortKey_WhenTheLabelPrefixIsTheKeyPrefix()
    {
        // A one-word repository name gives the same prefix to keys and labels.
        var single = Path.Combine(root, "frontend");
        await store.EnsureDefaultColumnsAsync(single, Ct);
        var cards = new List<BoardCardRecord>();
        for (var i = 0; i < 3; i++) cards.Add(await store.CreateCardAsync(single, Card(), Ct));
        Assert.All(cards, c => Assert.StartsWith("FRON-", c.Key));
        Assert.Equal(["FRON-1", "FRON-2", "FRON-3"], cards.Select(c => c.DisplayId));

        // Upgraded cards show their full keys and the label sequence starts empty, as after board/20.
        await Sql("UPDATE BoardCards SET DisplayId = CardKey; DELETE FROM BoardDisplaySequences;");
        var next = await store.CreateCardAsync(single, Card(), Ct);
        Assert.Equal(4, next.Number);
        Assert.Equal("FRON-4", next.DisplayId);
        Assert.Equal(cards[0].Id, (await store.FindCardAsync(single, "FRON-1", Ct))!.Id);
        Assert.Equal(cards[0].Id, (await store.FindCardAsync(single, "vb-1", Ct))!.Id);

        // A manual label may not take another card's short key (either alias), nor its full key.
        var taken = await Assert.ThrowsAsync<BoardConflictException>(() => store.UpdateCardAsync(single, next.Id, new(DisplayId: "FRON-2"), Ct));
        Assert.Contains("another card's key", taken.Message);
        await Assert.ThrowsAsync<BoardConflictException>(() => store.UpdateCardAsync(single, next.Id, new(DisplayId: "VB-3"), Ct));
        await Assert.ThrowsAsync<BoardConflictException>(() => store.UpdateCardAsync(single, next.Id, new(DisplayId: cards[1].Key), Ct));
        await Assert.ThrowsAsync<BoardConflictException>(() => store.CreateCardAsync(single, Card() with { DisplayId = "FRON-1" }, Ct));
        // Its own short key is fine.
        Assert.Equal("FRON-4", (await store.UpdateCardAsync(single, next.Id, new(DisplayId: "fron-4"), Ct))!.DisplayId);
        Assert.Equal(cards[1].Id, (await store.FindCardAsync(single, "FRON-2", Ct))!.Id);
    }

    [Fact]
    public async Task IncomingLabelThatSpellsAKeyGetsANewLabel_AndLocalKeysSkipNumbersALabelHolds()
    {
        var single = Path.Combine(root, "frontend");
        await store.EnsureDefaultColumnsAsync(single, Ct);
        var first = await store.CreateCardAsync(single, Card(), Ct);
        var second = await store.CreateCardAsync(single, Card(), Ct);

        // The server's label spells the second card's short key; keys never move, so the incoming card moves.
        var incoming = (await store.CreateSyncedCardAsync(single, "card_00000000abcd", "FRON-QWERT-3",
            Card("Remote") with { BoardId = first.BoardId, DisplayId = "FRON-2" }, BoardAuthor.User(),
            new("log_remote_label", 1, DateTime.UtcNow, BoardId: first.BoardId), Ct))!;
        Assert.NotEqual("FRON-2", incoming.DisplayId);
        Assert.Equal("FRON-2", (await store.FindCardAsync(single, second.Id, Ct))!.DisplayId);
        Assert.Equal(second.Id, (await store.FindCardAsync(single, "FRON-2", Ct))!.Id);
        Assert.Equal(incoming.Id, (await store.FindCardAsync(single, incoming.DisplayId, Ct))!.Id);

        // A label ahead of the key sequence: the key that would have spelled it is never minted.
        var ahead = incoming.Number + 2;
        await store.UpdateCardAsync(single, first.Id, new(DisplayId: $"FRON-{ahead}"), Ct);
        var a = await store.CreateCardAsync(single, Card(), Ct);
        var b = await store.CreateCardAsync(single, Card(), Ct);
        Assert.Equal(ahead - 1, a.Number);
        Assert.Equal(ahead + 1, b.Number);
        // Even with the label sequence ahead, a card's free short key is its label.
        Assert.Equal($"FRON-{a.Number}", a.DisplayId);
        Assert.Equal($"FRON-{b.Number}", b.DisplayId);
        Assert.Equal(first.Id, (await store.FindCardAsync(single, $"FRON-{ahead}", Ct))!.Id);
    }

    private async Task Sql(string commandText)
    {
        await using var db = new SqliteConnection(cs);
        await db.OpenAsync(Ct);
        var sql = db.CreateCommand();
        sql.CommandText = commandText;
        await sql.ExecuteNonQueryAsync(Ct);
    }

    [Fact]
    public async Task MaximumManualNumberDoesNotExhaustFutureLabels()
    {
        await store.EnsureDefaultColumnsAsync(project, Ct);
        var first = await store.CreateCardAsync(project, Card(), Ct);
        await store.UpdateCardAsync(project, first.Id, new(DisplayId: "VIBE-2147483647"), Ct);
        var next = await store.CreateCardAsync(project, Card(), Ct);
        Assert.StartsWith("VIBE-", next.DisplayId);
        Assert.NotEqual("VIBE-2147483647", next.DisplayId);
        Assert.Equal(next.Id, (await store.FindCardAsync(project, next.DisplayId, Ct))!.Id);
    }

    [Fact]
    public async Task UpgradePreservesLegacyKeysAndOlderWritersRemainReadable()
    {
        await store.EnsureDefaultColumnsAsync(project, Ct);
        var card = await store.CreateCardAsync(project, Card(), Ct);
        await using (var db = new SqliteConnection(cs))
        {
            await db.OpenAsync(Ct);
            var sql = db.CreateCommand();
            sql.CommandText = "UPDATE BoardCards SET DisplayId = NULL; DELETE FROM SchemaMigrations WHERE Component = 'board' AND Version = 20;";
            await sql.ExecuteNonQueryAsync(Ct);
        }
        var upgraded = new BoardStore(cs, cs);
        Assert.Equal(card.Key, (await upgraded.FindCardAsync(project, card.Id, Ct))!.DisplayId);
        await using (var db = new SqliteConnection(cs))
        {
            await db.OpenAsync(Ct);
            var sql = db.CreateCommand();
            // Models an older binary's insert: neither new column participates in its write.
            sql.CommandText = """
                INSERT INTO BoardCards (Id, ProjectPath, Number, ColumnId, Position, Title, Description, Priority, Tags, Blocked, CreatedUTC, UpdatedUTC)
                SELECT 'card_legacy', ProjectPath, 999, ColumnId, 0, 'Legacy', '', 'medium', '[]', 0, CreatedUTC, UpdatedUTC FROM BoardCards LIMIT 1;
                """;
            await sql.ExecuteNonQueryAsync(Ct);
        }
        var legacy = (await upgraded.FindCardAsync(project, "card_legacy", Ct))!;
        Assert.Equal(legacy.Key, legacy.DisplayId);
        Assert.Equal(legacy.Id, (await upgraded.FindCardAsync(project, legacy.DisplayId.ToLowerInvariant(), Ct))!.Id);
    }

    [Fact]
    public async Task IncomingServerLabelRenamesLocalOccupantAndQueuesCorrection()
    {
        await store.EnsureDefaultColumnsAsync(project, Ct);
        var local = await store.CreateCardAsync(project, Card(), Ct);
        var incoming = await store.CreateSyncedCardAsync(project, "card_123456abcdef", "VR-ABCDE-45",
            Card("Remote") with { BoardId = local.BoardId, DisplayId = local.DisplayId }, BoardAuthor.User(),
            new("log_remote", 1, DateTime.UtcNow, BoardId: local.BoardId), Ct);
        Assert.Equal("VIBE-1", incoming!.DisplayId);
        var renamed = (await store.FindCardAsync(project, local.Id, Ct))!;
        Assert.Equal("VIBE-2", renamed.DisplayId);
        Assert.Equal(local.Key, renamed.Key);
        Assert.Equal(incoming.Id, (await store.FindCardAsync(project, "VIBE-1", Ct))!.Id);
        Assert.Contains(await store.GetUnsentLogEntriesAsync(local.BoardId, 20, Ct),
            entry => entry.Entry.Kind == "change" && entry.Entry.Changes!.Contains("VIBE-2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DraftLinksAreAtomicAndProjectScoped()
    {
        await store.EnsureDefaultColumnsAsync(project, Ct);
        var target = await store.CreateCardAsync(project, Card("Target"), Ct);
        var otherProject = Path.Combine(root, "other");
        await store.EnsureDefaultColumnsAsync(otherProject, Ct);
        var foreign = await store.CreateCardAsync(otherProject, Card("Foreign"), Ct);
        var found = (await store.GetCardLinkCandidatesAsync(project, null, "", Ct))!;
        Assert.Equal(target.Id, Assert.Single(found).Id);
        await Assert.ThrowsAsync<BoardValidationException>(() => store.CreateCardAsync(project,
            Card("Failed") with { LinkedCardIds = [target.Id, foreign.Id] }, Ct));
        Assert.Single(await store.GetCardsAsync(project, Ct));
        Assert.Empty((await store.GetCardDetailAsync(project, target.Id, Ct))!.LinkedCards);
        var created = await store.CreateCardAsync(project, Card() with { LinkedCardIds = [target.Id, target.Id] }, Ct);
        Assert.Equal("VIBE-2", created.DisplayId);
        Assert.Equal(target.Id, Assert.Single((await store.GetCardDetailAsync(project, created.Id, Ct))!.LinkedCards).Id);
        Assert.Equal(created.Id, Assert.Single((await store.GetCardDetailAsync(project, target.Id, Ct))!.LinkedCards).Id);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(root, true);
    }
}
