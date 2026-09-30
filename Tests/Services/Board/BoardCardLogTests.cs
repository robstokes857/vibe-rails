using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using VibeRails.DTOs;
using VibeRails.Services.Board;
using Xunit;

namespace Tests.Services.Board;

/// <summary>
/// board/14 (VB-51) against a real temp-file database: a new card's stored random key, the Card
/// Log entries written in the same transaction as each card write, and soft delete.
/// </summary>
public sealed partial class BoardCardLogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"viberails-board-log-{Guid.NewGuid():N}");
    private readonly string _connectionString;
    private readonly BoardStore _store;
    private readonly string _project;

    public BoardCardLogTests()
    {
        Directory.CreateDirectory(_root);
        _project = Path.Combine(_root, "project-a");
        _connectionString = $"Data Source={Path.Combine(_root, "state.db")};Mode=ReadWriteCreate;Cache=Shared";
        _store = new BoardStore(_connectionString, _connectionString);
    }

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [GeneratedRegex(@"^[A-Z]{2,4}-[A-Z0-9]{5}-\d+$")]
    private static partial Regex StoredKeyShape();

    [Fact]
    public async Task NewCard_StoresAnUpperCaseRandomKey_ThatEveryFormFinds()
    {
        await _store.EnsureDefaultColumnsAsync(_project, Ct);
        var card = await _store.CreateCardAsync(_project, NewCard("Keyed"), Ct);

        Assert.Matches(StoredKeyShape(), card.Key);
        Assert.Equal(card.Key, card.StoredKey);
        Assert.EndsWith("-" + card.Number, card.Key);
        Assert.Equal(card.Key, await ScalarAsync("SELECT CardKey FROM BoardCards WHERE Id = $id", card.Id));

        var shortKey = BoardKeyText.Short(card.Key);
        foreach (var form in new[] { card.Key, card.Key.ToLowerInvariant(), " " + card.Key + " ", shortKey, shortKey.ToLowerInvariant(), card.Id })
            Assert.Equal(card.Id, (await _store.FindCardAsync(_project, form, Ct))?.Id);

        // A wrong random part is not the card, even though the number matches.
        var wrong = card.Key.Split('-')[0] + "-ZZZZZ-" + card.Number;
        if (wrong != card.Key)
            Assert.Null(await _store.FindCardAsync(_project, wrong, Ct));
    }

    [Fact]
    public async Task StoredKey_NeverChanges_AcrossEditsMovesAndReopening()
    {
        await _store.EnsureDefaultColumnsAsync(_project, Ct);
        var lanes = await _store.GetColumnsAsync(_project, Ct);
        var card = await _store.CreateCardAsync(_project, NewCard("Stable"), Ct);

        var edited = await _store.UpdateCardAsync(_project, card.Id, new BoardCardPatch(Title: "Renamed", Priority: "high"), Ct);
        var moved = await _store.MoveCardAsync(_project, card.Id, lanes[1].Id, null, Ct);
        var reopened = await new BoardStore(_connectionString, _connectionString).FindCardAsync(_project, card.Id, Ct);

        Assert.Equal(card.Key, edited!.Key);
        Assert.Equal(card.Key, moved!.Key);
        Assert.Equal(card.Key, reopened!.Key);
    }

    [Fact]
    public async Task EveryNewCard_GetsItsOwnKey()
    {
        await _store.EnsureDefaultColumnsAsync(_project, Ct);
        var keys = new List<string>();
        for (var i = 0; i < 12; i++)
            keys.Add((await _store.CreateCardAsync(_project, NewCard($"Card {i}"), Ct)).Key);

        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
        Assert.All(keys, key => Assert.Matches(StoredKeyShape(), key));
    }

    [Fact]
    public async Task LegacyCard_WithoutAStoredKey_KeepsItsComputedKey()
    {
        await _store.EnsureDefaultColumnsAsync(_project, Ct);
        var card = await _store.CreateCardAsync(_project, NewCard("Older binary"), Ct);
        await ExecuteAsync("UPDATE BoardCards SET CardKey = NULL WHERE Id = $id", card.Id);

        var found = await _store.FindCardAsync(_project, card.Id, Ct);
        var shortKey = BoardKeys.Format(card.KeyPrefix, card.Number);
        Assert.Null(found!.StoredKey);
        Assert.Equal(shortKey, found.Key);
        Assert.Equal(card.Id, (await _store.FindCardAsync(_project, shortKey, Ct))?.Id);
        Assert.Null(await _store.FindCardAsync(_project, card.Key, Ct));
    }

    [Fact]
    public async Task Create_WritesACreatedEntry_CarryingEveryField()
    {
        await _store.EnsureDefaultColumnsAsync(_project, Ct);
        var lanes = await _store.GetColumnsAsync(_project, Ct);
        var author = BoardAuthor.Agent("Claude", "claude", "sess-1");
        var card = await _store.CreateCardAsync(_project,
            new NewBoardCard(null, "Log me", "Body text", "base:claude", "high", 3, ["api", "ui"], true, Type: "bug", Flagged: true),
            Ct, author);

        var entry = Assert.Single((await _store.GetCardHistoryAsync(_project, card.Id, Ct)));
        Assert.Equal(BoardCommentKinds.Created, entry.Kind);
        Assert.Equal($"Created in {lanes[0].Name}", entry.Body);
        Assert.Equal(author, entry.Author);

        using var changes = JsonDocument.Parse(entry.Changes!);
        var root = changes.RootElement;
        Assert.Equal("Log me", To(root, "title").GetString());
        Assert.Equal("Body text", To(root, "description").GetString());
        Assert.Equal("bug", To(root, "type").GetString());
        Assert.Equal("high", To(root, "priority").GetString());
        Assert.Equal(3, To(root, "points").GetInt32());
        Assert.Equal("base:claude", To(root, "assignee").GetString());
        Assert.Equal(["api", "ui"], To(root, "tags").EnumerateArray().Select(t => t.GetString()!).ToArray());
        Assert.True(To(root, "blocked").GetBoolean());
        Assert.True(To(root, "flagged").GetBoolean());
        Assert.Equal(lanes[0].Id, To(root, "lane").GetString());
        Assert.All(root.EnumerateObject(), field => Assert.False(field.Value.TryGetProperty("from", out _)));
    }

    [Fact]
    public async Task Update_WritesOneChangeEntry_WithFromAndToForEachChangedField()
    {
        await _store.EnsureDefaultColumnsAsync(_project, Ct);
        var card = await _store.CreateCardAsync(_project, NewCard("Before") with { Points = 3 }, Ct);

        await _store.UpdateCardAsync(_project, card.Id,
            new BoardCardPatch(Title: "After", Description: "New text", Priority: "high", ClearPoints: true, Tags: ["x"], Blocked: true),
            Ct, BoardAuthor.User());

        var history = (await _store.GetCardHistoryAsync(_project, card.Id, Ct));
        Assert.Equal([BoardCommentKinds.Created, BoardCommentKinds.Change], history.Select(e => e.Kind).ToArray());
        var change = history[1];
        Assert.Equal(BoardAuthor.User(), change.Author);
        Assert.Equal("Title: Before → After · Description edited · Priority: medium → high · Points: 3 → none · Tags: none → x · Blocked", change.Body);

        using var changes = JsonDocument.Parse(change.Changes!);
        var root = changes.RootElement;
        Assert.Equal(["title", "description", "priority", "points", "tags", "blocked"], root.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal("Before", From(root, "title").GetString());
        Assert.Equal("After", To(root, "title").GetString());
        // A description carries only its new text; the old one is the previous entry's "to".
        Assert.False(root.GetProperty("description").TryGetProperty("from", out _));
        Assert.Equal("New text", To(root, "description").GetString());
        Assert.Equal(3, From(root, "points").GetInt32());
        Assert.Equal(JsonValueKind.Null, To(root, "points").ValueKind);
        Assert.False(From(root, "blocked").GetBoolean());
        Assert.True(To(root, "blocked").GetBoolean());
    }

    [Fact]
    public async Task MoveBetweenLanes_LogsLaneIds_AndLaneNamesInTheBody()
    {
        await _store.EnsureDefaultColumnsAsync(_project, Ct);
        var lanes = await _store.GetColumnsAsync(_project, Ct);
        var card = await _store.CreateCardAsync(_project, NewCard("Mover"), Ct);

        await _store.MoveCardAsync(_project, card.Id, lanes[1].Id, null, Ct, BoardAuthor.System());

        var move = (await _store.GetCardHistoryAsync(_project, card.Id, Ct))[^1];
        Assert.Equal(BoardCommentKinds.Change, move.Kind);
        Assert.Equal($"Moved {lanes[0].Name} → {lanes[1].Name}", move.Body);
        Assert.Equal(BoardAuthor.SystemKind, move.Author.Kind);
        using var changes = JsonDocument.Parse(move.Changes!);
        Assert.Equal(lanes[0].Id, From(changes.RootElement, "lane").GetString());
        Assert.Equal(lanes[1].Id, To(changes.RootElement, "lane").GetString());
    }

    [Fact]
    public async Task ReorderNoOpAndLaunchOptions_WriteNoEntry()
    {
        await _store.EnsureDefaultColumnsAsync(_project, Ct);
        var lanes = await _store.GetColumnsAsync(_project, Ct);
        var first = await _store.CreateCardAsync(_project, NewCard("First"), Ct);
        await _store.CreateCardAsync(_project, NewCard("Second"), Ct);

        await _store.MoveCardAsync(_project, first.Id, lanes[0].Id, 1, Ct);
        await _store.UpdateCardAsync(_project, first.Id, new BoardCardPatch(Title: "First", Priority: "medium"), Ct);
        await _store.UpdateCardAsync(_project, first.Id, new BoardCardPatch(BaseLlmOptions: new BaseLlmOptions(Model: "opus")), Ct);

        var history = (await _store.GetCardHistoryAsync(_project, first.Id, Ct));
        Assert.Equal([BoardCommentKinds.Created], history.Select(e => e.Kind).ToArray());
    }

    [Fact]
    public async Task LogEntries_StayOutOfCommentsNotesAndTheCommentCount()
    {
        await _store.EnsureDefaultColumnsAsync(_project, Ct);
        var card = await _store.CreateCardAsync(_project, NewCard("Counted"), Ct);
        await _store.UpdateCardAsync(_project, card.Id, new BoardCardPatch(Title: "Counted twice"), Ct);
        await _store.AddCommentAsync(_project, card.Id, BoardAuthor.User(), "A real comment", Ct);
        await _store.AddNoteAsync(_project, card.Id, BoardAuthor.Agent("Codex", "codex", null), "A note", Ct);

        var detail = (await _store.GetCardDetailAsync(_project, card.Id, Ct))!;
        Assert.Equal(2, detail.Comments.Count);
        Assert.Empty(detail.Notes);
        Assert.Equal(2, (await _store.GetCardHistoryAsync(_project, card.Id, Ct)).Count);
        Assert.Equal(2, detail.Card.CommentCount);
        Assert.Equal(2, Assert.Single(await _store.GetCardsAsync(_project, Ct)).CommentCount);
    }

    [Fact]
    public async Task LongChangeSummary_IsTruncated()
    {
        await _store.EnsureDefaultColumnsAsync(_project, Ct);
        var card = await _store.CreateCardAsync(_project, NewCard("Short"), Ct);
        var longTitle = new string('t', 1500);
        await _store.UpdateCardAsync(_project, card.Id, new BoardCardPatch(Title: longTitle), Ct);
        await _store.UpdateCardAsync(_project, card.Id, new BoardCardPatch(Title: longTitle + "!"), Ct);

        var change = (await _store.GetCardHistoryAsync(_project, card.Id, Ct))[^1];
        Assert.Equal(2000, change.Body.Length);
        Assert.EndsWith("…", change.Body);
        using var changes = JsonDocument.Parse(change.Changes!);
        Assert.Equal(longTitle + "!", To(changes.RootElement, "title").GetString());
    }

    [Fact]
    public async Task Delete_IsSoft_HidesTheCardEverywhere_AndLogsIt()
    {
        await _store.EnsureDefaultColumnsAsync(_project, Ct);
        var kept = await _store.CreateCardAsync(_project, NewCard("Kept"), Ct);
        var card = await _store.CreateCardAsync(_project, NewCard("Gone"), Ct);
        await _store.AddCommentAsync(_project, card.Id, BoardAuthor.User(), "Still here", Ct);

        Assert.True(await _store.DeleteCardAsync(_project, card.Id, Ct, BoardAuthor.Agent("Claude", "claude", "sess-2")));

        foreach (var form in new[] { card.Id, card.Key, BoardKeyText.Short(card.Key) })
        {
            Assert.Null(await _store.FindCardAsync(_project, form, Ct));
            Assert.Null(await _store.GetCardDetailAsync(_project, form, Ct));
        }
        Assert.Equal([kept.Id], (await _store.GetCardsAsync(_project, Ct)).Select(c => c.Id).ToArray());
        Assert.False(await _store.DeleteCardAsync(_project, card.Id, Ct));
        Assert.Null(await _store.UpdateCardAsync(_project, card.Id, new BoardCardPatch(Title: "Revived?"), Ct));

        // The row, its comment and its log stay; only DeletedUTC marks it.
        Assert.NotNull(await ScalarAsync("SELECT DeletedUTC FROM BoardCards WHERE Id = $id", card.Id));
        Assert.Equal("Still here", await ScalarAsync("SELECT Body FROM BoardComments WHERE CardId = $id AND Kind = 'comment'", card.Id));
        Assert.Equal("Deleted", await ScalarAsync("SELECT Body FROM BoardComments WHERE CardId = $id AND Kind = 'deleted'", card.Id));
        Assert.Equal("sess-2", await ScalarAsync("SELECT SessionId FROM BoardComments WHERE CardId = $id AND Kind = 'deleted'", card.Id));
    }

    [Fact]
    public async Task DeletedCard_KeepsItsNumber_SoTheNextCardDoesNotReuseIt()
    {
        await _store.EnsureDefaultColumnsAsync(_project, Ct);
        var first = await _store.CreateCardAsync(_project, NewCard("One"), Ct);
        var second = await _store.CreateCardAsync(_project, NewCard("Two"), Ct);
        await _store.DeleteCardAsync(_project, second.Id, Ct);

        var third = await _store.CreateCardAsync(_project, NewCard("Three"), Ct);

        Assert.True(third.Number > second.Number);
        Assert.NotEqual(BoardKeyText.Short(second.Key), BoardKeyText.Short(third.Key));
        Assert.Equal(first.Id, (await _store.FindCardAsync(_project, BoardKeyText.Short(first.Key), Ct))?.Id);
    }

    [Fact]
    public async Task ShortKey_PrefersTheLegacyCard_OverASyncedCardWithItsNumber_ButTwoStoredKeysStayAmbiguous()
    {
        await _store.EnsureDefaultColumnsAsync(_project, Ct);
        var legacy = await _store.CreateCardAsync(_project, NewCard("Legacy"), Ct);
        // A card an older binary numbered: no stored key, so PREFIX-n is the only key it has.
        await ExecuteAsync("UPDATE BoardCards SET CardKey = NULL WHERE Id = $id", legacy.Id);
        var prefix = legacy.KeyPrefix;
        var shortKey = BoardKeys.Format(prefix, legacy.Number);

        // A web-minted card whose number is at or below the local mark keeps its key and takes the next local number.
        var syncedKey = $"{prefix}-ZZZZZ-{legacy.Number}";
        var synced = (await _store.CreateSyncedCardAsync(_project, "card_0000000000a1", syncedKey,
            new(legacy.ColumnId, "Synced", "", null, "medium", null, [], false, BoardId: legacy.BoardId),
            BoardAuthor.System(), new("log_synced_a1", 1, DateTime.UtcNow, BoardId: legacy.BoardId), Ct))!;
        Assert.NotEqual(legacy.Number, synced.Number);

        Assert.Equal(legacy.Id, (await _store.FindCardAsync(_project, shortKey, Ct))?.Id);
        Assert.Equal(legacy.Id, (await _store.FindCardAsync(_project, shortKey.ToLowerInvariant(), Ct))?.Id);
        Assert.Equal(synced.Id, (await _store.FindCardAsync(_project, syncedKey, Ct))?.Id);
        Assert.Equal(synced.Id, (await _store.FindCardAsync(_project, syncedKey.ToLowerInvariant(), Ct))?.Id);

        // Two stored-key cards on one number both have full keys to fall back on, so the short form stays ambiguous.
        var first = (await _store.CreateSyncedCardAsync(_project, "card_0000000000b1", $"{prefix}-AAAAA-500",
            new(legacy.ColumnId, "First", "", null, "medium", null, [], false, BoardId: legacy.BoardId),
            BoardAuthor.System(), new("log_synced_b1", 2, DateTime.UtcNow, BoardId: legacy.BoardId), Ct))!;
        var second = (await _store.CreateSyncedCardAsync(_project, "card_0000000000b2", $"{prefix}-BBBBB-500",
            new(legacy.ColumnId, "Second", "", null, "medium", null, [], false, BoardId: legacy.BoardId),
            BoardAuthor.System(), new("log_synced_b2", 3, DateTime.UtcNow, BoardId: legacy.BoardId), Ct))!;
        Assert.Equal(500, first.Number);
        Assert.NotEqual(first.Number, second.Number);
        await Assert.ThrowsAsync<BoardValidationException>(() => _store.FindCardAsync(_project, BoardKeys.Format(prefix, 500), Ct));
        Assert.Equal(first.Id, (await _store.FindCardAsync(_project, first.Key, Ct))?.Id);
        Assert.Equal(second.Id, (await _store.FindCardAsync(_project, second.Key, Ct))?.Id);
    }

    private static NewBoardCard NewCard(string title) => new(null, title, "", null, "medium", null, [], false);

    private static JsonElement To(JsonElement root, string field) => root.GetProperty(field).GetProperty("to");

    private static JsonElement From(JsonElement root, string field) => root.GetProperty(field).GetProperty("from");

    private async Task<string?> ScalarAsync(string sql, string id)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", id);
        return await command.ExecuteScalarAsync(Ct) as string;
    }

    private async Task ExecuteAsync(string sql, string id)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(Ct);
    }

    public void Dispose()
    {
        SqliteConnection.ClearPool(new SqliteConnection(_connectionString));
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
