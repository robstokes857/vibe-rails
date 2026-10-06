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
    public async Task JiraFilterUsesIssueLinksAcrossPagedLanes_AndHumanFilterExcludesImports()
    {
        await _store.EnsureDefaultColumnsAsync(_project, Ct);
        var lane = (await _store.GetColumnsAsync(_project, Ct)).First(c => c.Name == "Done");
        var human = await _store.CreateCardAsync(_project, Card(lane.Id, "Human"), Ct);
        for (var index = 0; index < 3; index++)
            await _store.CreateJiraCardAsync(_project, Card(lane.Id, "Imported " + index),
                new BoardJiraLinkRecord("", "jira_test", index.ToString(), "TEST-" + index,
                    null, DateTime.UtcNow, DateTime.UtcNow), Ct);
        var first = await _store.GetCardsPageAsync(_project, new(2, lane.Id, Origin: "jira"), Ct);
        Assert.Equal(3, first.FilteredCount);
        Assert.Equal(2, first.Cards.Count);
        Assert.All(first.Cards, card => Assert.StartsWith("TEST-", card.JiraIssueKey));
        var second = await _store.GetCardsPageAsync(_project, new(2, lane.Id, Offset: 2, Origin: "jira"), Ct);
        Assert.Single(second.Cards);
        var humans = await _store.GetCardsPageAsync(_project, new(2, lane.Id, Origin: "human"), Ct);
        Assert.Equal(human.Id, Assert.Single(humans.Cards).Id);
    }

    [Fact]
    public async Task FlaggedCardsLeadEveryLaneAfterActivityMovesAndFlagChanges()
    {
        await _store.EnsureDefaultColumnsAsync(_project, Ct);
        var columns = await _store.GetColumnsAsync(_project, Ct);
        foreach (var lane in columns)
        {
            var firstFlag = await _store.CreateCardAsync(_project, Card(lane.Id, "First flag") with { Flagged = true }, Ct);
            var secondFlag = await _store.CreateCardAsync(_project, Card(lane.Id, "Second flag") with { Flagged = true }, Ct);
            var plain = await _store.CreateCardAsync(_project, Card(lane.Id, "Plain"), Ct);
            await _store.AddCommentAsync(_project, plain.Id, BoardAuthor.User(), "New activity", Ct);
            await _store.MoveCardAsync(_project, plain.Id, lane.Id, 0, Ct);
            await _store.MoveCardAsync(_project, secondFlag.Id, lane.Id, 99, Ct);
            var cards = (await _store.GetCardsAsync(_project, Ct)).Where(c => c.ColumnId == lane.Id).ToArray();
            Assert.Equal([firstFlag.Id, secondFlag.Id, plain.Id], cards.Select(c => c.Id));
            Assert.Equal([0, 1, 2], cards.Select(c => c.Position));
            var paged = await _store.GetCardsPageAsync(_project, new(1, lane.Id), Ct);
            Assert.Equal(firstFlag.Id, Assert.Single(paged.Cards).Id);

            await _store.UpdateCardAsync(_project, firstFlag.Id, new BoardCardPatch(Flagged: false), Ct);
            cards = (await _store.GetCardsAsync(_project, Ct)).Where(c => c.ColumnId == lane.Id).ToArray();
            Assert.Equal([secondFlag.Id, firstFlag.Id, plain.Id], cards.Select(c => c.Id));
            var changed = await _store.GetCardsPageAsync(_project,
                new(1, lane.Id, 1, ContinuationToken: paged.Lanes[0].ContinuationToken), Ct);
            Assert.True(changed.Lanes[0].RestartRequired);
            Assert.Equal(secondFlag.Id, Assert.Single(changed.Cards).Id);

            var otherLane = columns.First(c => c.Id != lane.Id);
            await _store.MoveCardAsync(_project, secondFlag.Id, otherLane.Id, 99, Ct);
            await _store.MoveCardAsync(_project, secondFlag.Id, lane.Id, 99, Ct);
            var reopened = new BoardStore(_connectionString, _connectionString);
            Assert.Equal(secondFlag.Id, (await reopened.GetCardsAsync(_project, Ct)).First(c => c.ColumnId == lane.Id).Id);
        }
    }

    [Fact]
    public async Task PagingPromotesLegacyFlagPositionsBeforeLimitingAndInvalidatesContinuation()
    {
        await _store.EnsureDefaultColumnsAsync(_project, Ct);
        var lane = (await _store.GetColumnsAsync(_project, Ct)).First(c => c.Name == "Done");
        var oldest = await _store.CreateCardAsync(_project, Card(lane.Id, "Flagged by an older writer"), Ct);
        for (var i = 0; i < 6; i++) await _store.CreateCardAsync(_project, Card(lane.Id, $"New {i}"), Ct);
        var first = await _store.GetCardsPageAsync(_project, new(2, lane.Id), Ct);
        // Disposable fixture only: model another version setting a flag without normalizing positions.
        await using (var connection = new SqliteConnection(_connectionString))
        {
            await connection.OpenAsync(Ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE BoardCards SET Flagged = 1 WHERE Id = $id;";
            command.Parameters.AddWithValue("$id", oldest.Id);
            await command.ExecuteNonQueryAsync(Ct);
        }
        var next = await _store.GetCardsPageAsync(_project,
            new(2, lane.Id, 2, ContinuationToken: first.Lanes[0].ContinuationToken), Ct);
        Assert.True(next.Lanes[0].RestartRequired);
        Assert.Equal(oldest.Id, next.Cards[0].Id);
        Assert.Equal(oldest.Id, (await _store.GetCardsAsync(_project, Ct))[0].Id);
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
        Assert.Equal(0, first.FlaggedCount);
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
    public async Task FlaggedCountSharesThePointsScope_OpenLanesOnly_AndFollowsFlagChanges()
    {
        await _store.EnsureDefaultColumnsAsync(_project, Ct);
        var columns = await _store.GetColumnsAsync(_project, Ct);
        var backlog = columns.First();
        var done = columns.First(c => c.Name == "Done");
        var openFlag = await _store.CreateCardAsync(_project, Card(backlog.Id, "Open flag") with { Flagged = true, Points = 3 }, Ct);
        var doneFlag = await _store.CreateCardAsync(_project, Card(done.Id, "Done flag") with { Flagged = true, Points = 5 }, Ct);
        await _store.CreateCardAsync(_project, Card(backlog.Id, "Open plain"), Ct);

        var page = await _store.GetCardsPageAsync(_project, new(30), Ct);
        Assert.Equal(1, page.FlaggedCount);
        Assert.Equal(3, page.RemainingPoints);

        await _store.UpdateCardAsync(_project, openFlag.Id, new BoardCardPatch(Flagged: false), Ct);
        Assert.Equal(0, (await _store.GetCardsPageAsync(_project, new(30), Ct)).FlaggedCount);

        await _store.MoveCardAsync(_project, doneFlag.Id, backlog.Id, 0, Ct);
        Assert.Equal(1, (await _store.GetCardsPageAsync(_project, new(30), Ct)).FlaggedCount);
    }

    [Fact]
    public async Task FiltersReachUnloadedCards_AndStatsAndChoicesCoverTheWholeBoard()
    {
        await _store.EnsureDefaultColumnsAsync(_project, Ct);
        var columns = await _store.GetColumnsAsync(_project, Ct);
        var done = columns.First(c => c.Name == "Done");
        var needle = await _store.CreateCardAsync(_project, Card(done.Id, "Hidden oldest") with
        { Description = "Literal 100%_needle", Assignee = "base:claude", Type = "bug", Priority = "high", Tags = ["debug"], Points = 90, Blocked = true, Flagged = true }, Ct);
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
        Assert.Equal(0, result.FlaggedCount); // flagged, but Done: outside the attention total like its points
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
    public async Task OriginFilter_SeparatesAgentMadeCards_AndLeavesOlderCardsHuman()
    {
        await _store.EnsureDefaultColumnsAsync(_project, Ct);
        var lane = (await _store.GetColumnsAsync(_project, Ct)).First();
        var human = await _store.CreateCardAsync(_project, Card(lane.Id, "Typed by hand"), Ct);
        var agent = await _store.CreateCardAsync(_project, Card(lane.Id, "Filed by an agent") with { AgentMade = true }, Ct,
            BoardAuthor.Agent("Codex", "codex", null));

        Assert.False(human.AgentMade);
        Assert.True(agent.AgentMade);
        Assert.False((await _store.FindCardAsync(_project, human.Key, Ct))!.AgentMade);
        Assert.True((await _store.FindCardAsync(_project, agent.Key, Ct))!.AgentMade);

        var agents = await _store.GetCardsPageAsync(_project, new(Origin: "agent"), Ct);
        Assert.Equal(agent.Id, Assert.Single(agents.Cards).Id);
        Assert.Equal(1, agents.FilteredCount);
        Assert.Equal(2, agents.TotalCount);

        var people = await _store.GetCardsPageAsync(_project, new(Origin: "human"), Ct);
        Assert.Equal(human.Id, Assert.Single(people.Cards).Id);

        var either = await _store.GetCardsPageAsync(_project, new(), Ct);
        Assert.Equal(2, either.Cards.Count);
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
