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
        await store.RenameBoardAsync(project, board.Id, board.Name, Ct, "vb");
        var custom = await store.CreateCardAsync(project, Card(), Ct);
        Assert.Equal("VB-1", custom.DisplayId);
        Assert.Equal("VIBE-1", (await store.FindCardAsync(project, first.Id, Ct))!.DisplayId);
        await store.DeleteCardAsync(project, custom.Id, Ct);
        var token = Ct;
        var cards = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => store.CreateCardAsync(project, Card(), token), token)));
        Assert.Equal(8, cards.Select(c => c.DisplayId).Distinct().Count());
        Assert.DoesNotContain(cards, c => c.DisplayId == "VB-1");
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
