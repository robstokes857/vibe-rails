using Microsoft.Data.Sqlite;
using VibeRails.Services.Board;
using Xunit;

namespace Tests.Services.Board;

public sealed class BoardCardPagingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"viberails-board-pages-{Guid.NewGuid():N}");
    private readonly string _project;
    private readonly string _connectionString;
    private readonly BoardStore _store;
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public BoardCardPagingTests()
    {
        Directory.CreateDirectory(_root);
        _project = Path.Combine(_root, "project");
        _connectionString = $"Data Source={Path.Combine(_root, "board.db")};Cache=Private";
        _store = new(_connectionString, _connectionString);
    }

    [Fact]
    public async Task CompletedLanes_PageInStableOrder_WhileOpenCardsStayComplete()
    {
        await _store.EnsureDefaultColumnsAsync(_project, Ct);
        var columns = await _store.GetColumnsAsync(_project, Ct);
        var done = columns.First(c => c.Name == "Done");
        var open = columns.First();
        for (var index = 0; index < 103; index++)
            await _store.CreateCardAsync(_project, Card(done.Id, $"Done {index}") with
            { Description = new string('x', 2_000), Assignee = index == 0 ? "base:claude" : "base:codex", Tags = index == 0 ? ["oldest"] : [] }, Ct);
        for (var index = 0; index < 35; index++)
            await _store.CreateCardAsync(_project, Card(open.Id, $"Open {index}") with { Points = 2, Blocked = index == 0 }, Ct);

        var first = await _store.GetCardsPageAsync(_project, new(30), Ct);
        Assert.Equal(65, first.Cards.Count);
        Assert.Equal(35, first.Cards.Count(c => c.ColumnId == open.Id));
        Assert.Equal(138, first.TotalCount);
        Assert.Equal(138, first.FilteredCount);
        Assert.Equal(70, first.RemainingPoints);
        Assert.Equal(1, first.BlockedCount);
        Assert.Contains("oldest", first.Tags);
        Assert.Contains("base:claude", first.Assignees);
        var firstLane = Assert.Single(first.Lanes, lane => lane.ColumnId == done.Id);
        Assert.NotNull(firstLane.ContinuationToken);
        Assert.Equal(new BoardCardLanePage(done.Id, 103, 103, 30, true, firstLane.ContinuationToken), firstLane);
        var allDone = first.Cards.Where(c => c.ColumnId == done.Id).Select(c => c.Id).ToList();
        var next = firstLane;
        while (next.HasMore)
        {
            var page = await _store.GetCardsPageAsync(_project,
                new(30, done.Id, next.NextOffset, ContinuationToken: next.ContinuationToken), Ct);
            Assert.All(page.Cards, card => Assert.Equal(done.Id, card.ColumnId));
            Assert.InRange(page.Cards.Count, 1, 30);
            Assert.Equal(138, page.TotalCount);
            allDone.AddRange(page.Cards.Select(c => c.Id));
            next = Assert.Single(page.Lanes);
            Assert.False(next.RestartRequired);
        }
        Assert.Equal(103, next.NextOffset);
        var legacy = await _store.GetCardsAsync(_project, Ct);
        Assert.Equal(138, legacy.Count);
        Assert.Equal(legacy.Where(c => c.ColumnId == done.Id).Select(c => c.Id), allDone);
        Assert.Equal(103, allDone.Distinct().Count());
    }

    [Fact]
    public async Task ChangedActivityOrder_RestartsContinuationBeforeAnUnloadedCardCanBeOmitted()
    {
        await _store.EnsureDefaultColumnsAsync(_project, Ct);
        var done = (await _store.GetColumnsAsync(_project, Ct)).First(c => c.Name == "Done");
        for (var index = 0; index < 8; index++)
            await _store.CreateCardAsync(_project, Card(done.Id, $"Done {index}"), Ct);

        var first = await _store.GetCardsPageAsync(_project, new(3), Ct);
        var firstLane = Assert.Single(first.Lanes, lane => lane.ColumnId == done.Id);
        Assert.NotNull(firstLane.ContinuationToken);
        var unloaded = (await _store.GetCardsAsync(_project, Ct))
            .First(card => card.ColumnId == done.Id && first.Cards.All(loaded => loaded.Id != card.Id));

        await _store.AddCommentAsync(_project, unloaded.Id, BoardAuthor.User(), "Promote this card", Ct);

        var stale = await _store.GetCardsPageAsync(_project,
            new(3, done.Id, firstLane.NextOffset, ContinuationToken: firstLane.ContinuationToken), Ct);
        var restartedLane = Assert.Single(stale.Lanes);
        Assert.True(restartedLane.RestartRequired);
        Assert.NotEqual(firstLane.ContinuationToken, restartedLane.ContinuationToken);
        Assert.Equal(unloaded.Id, stale.Cards[0].Id);
        Assert.Equal(3, restartedLane.NextOffset);

        var allIds = stale.Cards.Select(card => card.Id).ToList();
        var continuation = restartedLane;
        while (continuation.HasMore)
        {
            var page = await _store.GetCardsPageAsync(_project,
                new(3, done.Id, continuation.NextOffset, ContinuationToken: continuation.ContinuationToken), Ct);
            continuation = Assert.Single(page.Lanes);
            Assert.False(continuation.RestartRequired);
            allIds.AddRange(page.Cards.Select(card => card.Id));
        }

        Assert.Equal(8, allIds.Count);
        Assert.Equal(8, allIds.Distinct().Count());
        Assert.Contains(unloaded.Id, allIds);
    }

    [Fact]
    public async Task FiltersReachUnloadedCards_AndStatsAndChoicesCoverTheWholeBoard()
    {
        await _store.EnsureDefaultColumnsAsync(_project, Ct);
        var columns = await _store.GetColumnsAsync(_project, Ct);
        var done = columns.First(c => c.Name == "Done");
        var needle = await _store.CreateCardAsync(_project, Card(done.Id, "Hidden oldest") with
        { Description = "Literal 100%_needle", Assignee = "base:claude", Type = "bug", Priority = "high", Tags = ["debug"], Points = 90, Blocked = true }, Ct);
        for (var index = 0; index < 35; index++)
            await _store.CreateCardAsync(_project, Card(done.Id, $"New {index}"), Ct);
        await _store.CreateCardAsync(_project, Card(columns.First().Id, "Open bug") with
        { Assignee = "base:codex", Tags = ["open"], Type = "bug", Points = 3 }, Ct);

        var query = new BoardCardPageQuery(30, Q: "100%_needle", Assignee: "base:claude", Type: "bug", Priority: "high", Tag: "debug");
        var result = await _store.GetCardsPageAsync(_project, query, Ct);
        Assert.Equal(needle.Id, Assert.Single(result.Cards).Id);
        Assert.Equal(37, result.TotalCount);
        Assert.Equal(1, result.FilteredCount);
        Assert.Equal(1, result.BlockedCount);
        Assert.Equal(0, result.RemainingPoints);
        Assert.Equal(["debug", "open"], result.Tags);
        Assert.Equal(["base:claude", "base:codex"], result.Assignees);
        var filteredLane = Assert.Single(result.Lanes, lane => lane.ColumnId == done.Id);
        Assert.NotNull(filteredLane.ContinuationToken);
        Assert.Equal(new BoardCardLanePage(done.Id, 36, 1, 1, false, filteredLane.ContinuationToken), filteredLane);
        Assert.Empty((await _store.GetCardsPageAsync(_project, query with { Tag = "de" }, Ct)).Cards);
        var byKey = await _store.GetCardsPageAsync(_project, new(Q: needle.Key), Ct);
        Assert.Contains(byKey.Cards, card => card.Id == needle.Id);
        Assert.All(byKey.Cards, card => Assert.Contains(needle.Key, card.Key));
    }

    [Fact]
    public async Task SearchMatchesTheDisplayedCardTypeLabel()
    {
        await _store.EnsureDefaultColumnsAsync(_project, Ct);
        var lane = (await _store.GetColumnsAsync(_project, Ct)).First();
        var card = await _store.CreateCardAsync(_project, Card(lane.Id, "Investigation") with { Type = "research-spike" }, Ct);
        var page = await _store.GetCardsPageAsync(_project, new(Q: "Research spike"), Ct);
        Assert.Equal(card.Id, Assert.Single(page.Cards).Id);
    }

    [Fact]
    public async Task PagesAndMetadataNeverCrossBoardOrProjectScope()
    {
        await _store.EnsureDefaultColumnsAsync(_project, Ct);
        var main = (await _store.GetColumnsAsync(_project, Ct)).First();
        await _store.CreateCardAsync(_project, Card(main.Id, "Main") with { Tags = ["main"] }, Ct);
        var secondBoard = await _store.CreateBoardAsync(_project, "Second", Ct);
        var secondLane = (await _store.GetColumnsAsync(_project, Ct, secondBoard.Id)).First();
        await _store.CreateCardAsync(_project, Card(secondLane.Id, "Second") with { Tags = ["second"] }, Ct);
        var foreignProject = Path.Combine(_root, "foreign");
        await _store.EnsureDefaultColumnsAsync(foreignProject, Ct);
        var foreignLane = (await _store.GetColumnsAsync(foreignProject, Ct)).First();
        await _store.CreateCardAsync(foreignProject, Card(foreignLane.Id, "Foreign") with { Tags = ["foreign"] }, Ct);

        var mainPage = await _store.GetCardsPageAsync(_project, new(), Ct);
        Assert.Equal("Main", Assert.Single(mainPage.Cards).Title);
        Assert.Equal(["main"], mainPage.Tags);
        var secondPage = await _store.GetCardsPageAsync(_project, new(), Ct, secondBoard.Id);
        Assert.Equal("Second", Assert.Single(secondPage.Cards).Title);
        Assert.Equal(["second"], secondPage.Tags);
        await Assert.ThrowsAsync<BoardValidationException>(() => _store.GetCardsPageAsync(_project, new(ColumnId: secondLane.Id), Ct));
        await Assert.ThrowsAsync<BoardValidationException>(() => _store.GetCardsPageAsync(_project, new(ColumnId: foreignLane.Id), Ct));
        await Assert.ThrowsAsync<BoardValidationException>(() => _store.GetCardsPageAsync(foreignProject, new(), Ct, secondBoard.Id));
    }

    [Theory]
    [InlineData("Shipped")]
    [InlineData("Complete")]
    [InlineData("DONE")]
    public async Task CompletedLaneNamesAndPageBoundsMatchTheUi(string name)
    {
        await _store.EnsureDefaultColumnsAsync(_project, Ct);
        var lane = await _store.CreateColumnAsync(_project, name, "#888888", Ct);
        for (var index = 0; index < 3; index++)
            await _store.CreateCardAsync(_project, Card(lane.Id, $"Card {index}") with { Points = 5 }, Ct);
        var first = await _store.GetCardsPageAsync(_project, new(0, lane.Id, -1), Ct);
        Assert.Single(first.Cards);
        Assert.Equal(1, Assert.Single(first.Lanes).NextOffset);
        Assert.Equal(0, first.RemainingPoints);
        var end = await _store.GetCardsPageAsync(_project, new(30, lane.Id, int.MaxValue), Ct);
        Assert.Empty(end.Cards);
        var endLane = Assert.Single(end.Lanes);
        Assert.NotNull(endLane.ContinuationToken);
        Assert.Equal(new BoardCardLanePage(lane.Id, 3, 3, 3, false, endLane.ContinuationToken), endLane);
    }

    private static NewBoardCard Card(string laneId, string title) => new(laneId, title, "", null, "medium", null, [], false);

    public void Dispose()
    {
        SqliteConnection.ClearPool(new SqliteConnection(_connectionString));
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}
