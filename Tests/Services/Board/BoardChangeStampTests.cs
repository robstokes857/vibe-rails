using Microsoft.Data.Sqlite;
using Moq;
using VibeRails.DTOs;
using VibeRails.Services.Board;
using Xunit;

namespace Tests.Services.Board;

/// <summary>
/// VIBE-322: the Board poll skips boards, lanes and card pages, and the editor its card read, while
/// the change stamp holds. A write the Board shows that does not move it leaves a stale Board.
/// </summary>
public sealed class BoardChangeStampTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"viberails-change-stamp-{Guid.NewGuid():N}");
    private readonly string _connectionString;
    private readonly string _project;
    private readonly BoardStore _store;
    private readonly FakeLiveProbe _live = new();
    private readonly BoardService _service;
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public BoardChangeStampTests()
    {
        Directory.CreateDirectory(_root);
        _project = Path.Combine(_root, "project");
        _connectionString = $"Data Source={Path.Combine(_root, "board.db")};Pooling=False";
        _store = new BoardStore(_connectionString, _connectionString);
        _service = new BoardService(_store, new Mock<IBoardCommitService>(MockBehavior.Strict).Object, _live);
    }

    [Fact]
    public async Task EveryBoardTableIsStampedOrDeclaredUnshown()
    {
        await _store.EnsureDefaultColumnsAsync(_project, Ct);
        var tables = await NamesAsync("SELECT name FROM sqlite_schema WHERE type = 'table' AND name NOT LIKE 'sqlite_%'");
        var triggers = (await NamesAsync("SELECT name FROM sqlite_schema WHERE type = 'trigger' AND name LIKE 'BoardChangeStamp_%'")).ToHashSet();
        Assert.Empty(BoardStore.ChangeStampTables.Intersect(BoardStore.UnstampedTables));
        // A new board.db table must say whether the Board poll or the card editor shows it: add a
        // migration with its triggers, or list it in BoardStore.UnstampedTables with the reason.
        Assert.Empty(tables.Except(BoardStore.ChangeStampTables).Except(BoardStore.UnstampedTables));
        Assert.Empty(BoardStore.ChangeStampTables.Concat(BoardStore.UnstampedTables).Except(tables));
        foreach (var table in BoardStore.ChangeStampTables)
            foreach (var operation in new[] { "Insert", "Update", "Delete" })
                Assert.Contains($"BoardChangeStamp_{table}_{operation}", triggers);
        Assert.Equal(BoardStore.ChangeStampTables.Count * 3, triggers.Count);
    }

    [Fact]
    public async Task EveryWriteATileOrTheEditorShowsMovesTheStamp()
    {
        await _store.EnsureDefaultColumnsAsync(_project, Ct);
        var board = (await _store.GetBoardsAsync(_project, Ct))[0];
        var lanes = await _store.GetColumnsAsync(_project, Ct, board.Id);
        var card = await _store.CreateCardAsync(_project, new(null, "First", "", null, "medium", null, [], false), Ct);
        var other = await _store.CreateCardAsync(_project, new(null, "Second", "", null, "medium", null, [], false), Ct);
        const string session = "66666666-6666-4666-8666-666666666666";
        var writes = new (string Name, Func<Task> Write)[]
        {
            ("card update", () => _store.UpdateCardAsync(_project, card.Id, new(Title: "Renamed"), Ct)),
            ("comment", () => _store.AddCommentAsync(_project, card.Id, BoardAuthor.User(), "A comment", Ct)),
            ("move", () => _store.MoveCardAsync(_project, card.Id, lanes[1].Id, null, Ct)),
            ("pin", () => _store.SetCardPinnedAsync(_project, card.Id, true, Ct)),
            ("session link", () => _store.LinkSessionAsync(_project, card.Id, session, "tab", "", "codex", "CLI", "launch", Ct)),
            ("code link", () => _store.AddCodeLinkAsync(_project, card.Id, "https://github.com/org/repo/pull/7", Ct)),
            ("card link", () => _store.LinkCardAsync(_project, card.Id, other.Id, Ct)),
            ("lane rename", () => _store.UpdateColumnAsync(_project, lanes[0].Id, "Inbox", null, Ct)),
            ("new lane", () => _store.CreateColumnAsync(_project, "Later", "#64748b", Ct, board.Id)),
            ("board rename", () => _store.RenameBoardAsync(_project, board.Id, "Renamed board", null, Ct)),
            // Another root, an older binary or a sync writer: no BoardStore method involved.
            ("raw SQL", () => ExecuteAsync("UPDATE BoardCards SET Priority = 'high' WHERE Id = $id", other.Id)),
            ("raw delete", () => ExecuteAsync("DELETE FROM BoardCardLinks")),
        };
        foreach (var (name, write) in writes)
        {
            var before = await _store.GetChangeStampAsync(Ct);
            Assert.Matches("^[0-9a-f]{16}$", before);
            await write();
            Assert.True(before != await _store.GetChangeStampAsync(Ct), $"{name} did not move the change stamp");
        }
    }

    [Fact]
    public async Task HotWritesTheBoardDoesNotReadLeaveTheStampAlone()
    {
        await _store.EnsureDefaultColumnsAsync(_project, Ct);
        var card = await _store.CreateCardAsync(_project, new(null, "Desktop work", "", null, "medium", null, [], false), Ct);
        var before = await _store.GetChangeStampAsync(Ct);
        var expiry = DateTime.UtcNow.AddMinutes(5);
        Assert.True(await _store.StartDesktopActivityAsync(_project, card.Id, "client", "Codex", expiry, Ct));
        await _store.RenewDesktopActivityAsync("client", expiry.AddMinutes(1), Ct);
        await _store.EndDesktopActivityAsync("client", card.Id, Ct);
        _ = await _store.GetCardsPageAsync(_project, new(30), Ct);
        _ = await _store.GetCardDetailAsync(_project, card.Id, Ct);
        Assert.Equal(before, await _store.GetChangeStampAsync(Ct));
    }

    [Fact]
    public async Task ActivityCarriesTheTokenAndACardStampThatFollowsLiveState()
    {
        var card = await _service.CreateCardAsync(_project, new(Title: "Watched"), Ct);
        var other = await _service.CreateCardAsync(_project, new(Title: "Quiet"), Ct);
        var request = new BoardCardActivityRequest(card.BoardId, [card.Id, other.Id]);
        var first = await _service.GetCardActivityAsync(_project, request, Ct);
        Assert.Equal(await _store.GetChangeStampAsync(Ct), first.ChangeToken);
        var again = await _service.GetCardActivityAsync(_project, request, Ct);
        Assert.Equal(first.ChangeToken, again.ChangeToken);
        Assert.Equal(Stamp(first, card.Id), Stamp(again, card.Id));
        Assert.NotEqual(Stamp(first, card.Id), Stamp(first, other.Id));

        // A live tab changes no board.db row, so only the card stamp moves.
        const string session = "88888888-8888-4888-8888-888888888888";
        await _store.LinkSessionAsync(_project, card.Id, session, null, "", "codex", "CLI", "launch", Ct);
        var linked = await _service.GetCardActivityAsync(_project, request, Ct);
        Assert.NotEqual(first.ChangeToken, linked.ChangeToken);
        _live.Sessions[session] = "tab-1";
        var live = await _service.GetCardActivityAsync(_project, request, Ct);
        Assert.Equal(linked.ChangeToken, live.ChangeToken);
        Assert.NotEqual(Stamp(linked, card.Id), Stamp(live, card.Id));
        _live.Sessions[session] = "tab-2";
        Assert.NotEqual(Stamp(live, card.Id), Stamp(await _service.GetCardActivityAsync(_project, request, Ct), card.Id));

        // So does a desktop lease, which ends by expiring rather than by a write.
        var withoutDesktop = await _service.GetCardActivityAsync(_project, request, Ct);
        Assert.True(await _store.StartDesktopActivityAsync(_project, other.Id, "client", "Codex", DateTime.UtcNow.AddMinutes(5), Ct));
        var withDesktop = await _service.GetCardActivityAsync(_project, request, Ct);
        Assert.Equal(withoutDesktop.ChangeToken, withDesktop.ChangeToken);
        Assert.NotEqual(Stamp(withoutDesktop, other.Id), Stamp(withDesktop, other.Id));
        Assert.Equal(Stamp(withoutDesktop, card.Id), Stamp(withDesktop, card.Id));
    }

    [Fact]
    public async Task AStoreThatCannotTellSendsNoTokenOrStamps()
    {
        var card = await _service.CreateCardAsync(_project, new(Title: "Watched"), Ct);
        await ExecuteAsync("DELETE FROM BoardChangeStamp");
        var activity = await _service.GetCardActivityAsync(_project, new(card.BoardId, [card.Id]), Ct);
        Assert.Null(activity.ChangeToken);
        Assert.Null(Assert.Single(activity.Cards).Stamp);
        // A missing row never fails an older or newer writer's card write.
        await ExecuteAsync("UPDATE BoardCards SET Title = 'Still writable' WHERE Id = $id", card.Id);
    }

    [Fact]
    public async Task MigrationIsAdditiveAndAppliesToAnExistingFile()
    {
        var card = await _service.CreateCardAsync(_project, new(Title: "Before the stamp"), Ct);
        var drops = string.Concat(BoardStore.ChangeStampTables.SelectMany(table => new[] { "Insert", "Update", "Delete" }
            .Select(operation => $"DROP TRIGGER BoardChangeStamp_{table}_{operation};")));
        await ExecuteAsync(drops + "DROP TABLE BoardChangeStamp; DELETE FROM SchemaMigrations WHERE Component = 'board-change-stamp';");
        var upgraded = new BoardStore(_connectionString, _connectionString);
        var before = await upgraded.GetChangeStampAsync(Ct);
        Assert.NotNull(before);
        Assert.Equal("Before the stamp", (await upgraded.FindCardAsync(_project, card.Id, Ct))!.Title);
        await upgraded.UpdateCardAsync(_project, card.Id, new(Title: "After"), Ct);
        Assert.NotEqual(before, await upgraded.GetChangeStampAsync(Ct));
        // Opening it again changes nothing.
        var reopened = new BoardStore(_connectionString, _connectionString);
        Assert.Equal(await upgraded.GetChangeStampAsync(Ct), await reopened.GetChangeStampAsync(Ct));
    }

    private static string? Stamp(BoardCardActivityListResponse activity, string cardId) =>
        activity.Cards.Single(card => card.Id == cardId).Stamp;

    private async Task<List<string>> NamesAsync(string sql)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct)) names.Add(reader.GetString(0));
        return names;
    }

    private async Task ExecuteAsync(string sql, string? id = null)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (id is not null) command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(Ct);
    }

    private sealed class FakeLiveProbe : IBoardLiveSessionProbe
    {
        public Dictionary<string, string> Sessions { get; } = new(StringComparer.Ordinal);

        public Task<IReadOnlyDictionary<string, string>> GetLiveSessionsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(Sessions);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}
