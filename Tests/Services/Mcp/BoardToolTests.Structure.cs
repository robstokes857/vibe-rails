using Moq;
using VibeRails.DTOs;
using VibeRails.Services.Board;
using VibeRails.Services.Mcp.Tools;
using VibeRails.Services.Terminal;
using Xunit;

namespace Tests.Services.Mcp;

/// <summary>
/// Lanes and boards over MCP (VIBE-342): what the dashboard's + Lane, lane settings, lane drag,
/// Create board and board rename do, with the same validation, and a reply that says when a
/// change alters which lane is finished or last. Nothing deletes.
/// </summary>
public sealed partial class BoardToolTests
{
    private async Task<List<string>> LaneNamesAsync(string? boardId = null) =>
        (await _service.GetColumnsAsync(_project, Ct, boardId)).Columns.OrderBy(c => c.Position).Select(c => c.Name).ToList();

    [Fact]
    public async Task CreateBoardColumn_PlacesTheLane_AndSaysWhenTheLastLaneChanges()
    {
        await _store.EnsureDefaultColumnsAsync(_project, Ct);

        var placed = await _tool.CreateBoardColumn("QA", color: "#A855F7", position: 4, cancellationToken: Ct);
        Assert.StartsWith("Created lane \"QA\" (id ", placed);
        Assert.Contains(" at place 4 of 6.", placed);
        // Done is still last, so nothing about the completion check moved.
        Assert.DoesNotContain("last lane", placed);
        Assert.Equal(["Backlog", "Ready", "Build", "QA", "Review", "Done"], await LaneNamesAsync());
        Assert.Equal("#a855f7", (await _service.FindColumnAsync(_project, "QA", Ct))!.Color);

        // Without a place the lane goes to the end, as + Lane does, and that makes it the last lane.
        var appended = await _tool.CreateBoardColumn("Archive", cancellationToken: Ct);
        Assert.Contains(" as lane 7.", appended);
        Assert.Contains("The board's last lane is now \"Archive\" (it was \"Done\")", appended);
        Assert.Equal("Archive", (await LaneNamesAsync())[^1]);

        var first = await _tool.CreateBoardColumn("Inbox", defaultForNewCards: true, position: 1, cancellationToken: Ct);
        Assert.Contains(" at place 1 of 8; new cards land here.", first);
        await _tool.CreateBoardCard("Lands in the inbox", cancellationToken: Ct);
        var inbox = (await _service.FindColumnAsync(_project, "Inbox", Ct))!;
        Assert.Equal(inbox.Id, (await _service.GetCardAsync(_project, "PROJ-1", Ct))!.ColumnId);
        var listed = (await _tool.ListBoardColumns(cancellationToken: Ct)).Split('\n');
        Assert.Contains("default lane for new cards", Assert.Single(listed, line => line.StartsWith("- Inbox ")));
        Assert.Single(listed, line => line.Contains("default lane for new cards"));

        var finished = await _tool.CreateBoardColumn("Shipped to customers", color: "teal", position: 99, cancellationToken: Ct);
        Assert.Contains(" at place 9 of 9.", finished);
        Assert.Contains("The colour was not a hex colour like #3b82f6, so the lane is #64748b.", finished);
        Assert.Contains("The name makes it a finished lane", finished);
        Assert.Contains("The board's last lane is now \"Shipped to customers\" (it was \"Archive\")", finished);

        Assert.Equal("FAIL: a lane needs a name.", await _tool.CreateBoardColumn("  ", cancellationToken: Ct));
        Assert.Equal("FAIL: position counts from 1.", await _tool.CreateBoardColumn("Nowhere", position: 0, cancellationToken: Ct));
        Assert.StartsWith("FAIL: board not found: nope", await _tool.CreateBoardColumn("Nowhere", board: "nope", cancellationToken: Ct));
        Assert.Equal(9, (await LaneNamesAsync()).Count);
    }

    [Fact]
    public async Task UpdateBoardColumn_RenamesRecoloursAndSetsTheDefault_AndCardsStay()
    {
        await _tool.CreateBoardCard("Stays put", column: "Review", cancellationToken: Ct);
        var review = (await _service.FindColumnAsync(_project, "Review", Ct))!;

        // By name, whatever its case; the id never changes, so the card does not move.
        var renamed = await _tool.UpdateBoardColumn("review", name: "Code review", color: "#123ABC", defaultForNewCards: true, cancellationToken: Ct);
        Assert.Equal($"Updated lane \"Code review\" (id {review.Id}): renamed from \"Review\"; colour #123abc; new cards land here.", renamed);
        Assert.Equal(review.Id, (await _service.GetCardAsync(_project, "PROJ-1", Ct))!.ColumnId);
        Assert.Contains("- Code review (id " + review.Id + ", 1 card, default lane for new cards)", await _tool.ListBoardColumns(cancellationToken: Ct));

        // By id, one field: the name and colour are kept.
        Assert.Equal($"Updated lane \"Code review\" (id {review.Id}); new cards no longer land here.",
            await _tool.UpdateBoardColumn(review.Id, defaultForNewCards: false, cancellationToken: Ct));
        var kept = (await _service.FindColumnAsync(_project, review.Id, Ct))!;
        Assert.Equal(("Code review", "#123abc", false), (kept.Name, kept.Color, kept.IsDefaultForNewCards));

        Assert.Equal("FAIL: Lane color must be a hex color like #3b82f6.", await _tool.UpdateBoardColumn("Code review", color: "teal", cancellationToken: Ct));
        Assert.Equal("FAIL: Lane name cannot be empty.", await _tool.UpdateBoardColumn("Code review", name: "   ", cancellationToken: Ct));
        Assert.Equal("FAIL: nothing to change. Pass name, color, defaultForNewCards or position.", await _tool.UpdateBoardColumn("Code review", cancellationToken: Ct));
        Assert.StartsWith("FAIL: lane not found: Nowhere.", await _tool.UpdateBoardColumn("Nowhere", name: "Somewhere", cancellationToken: Ct));
        Assert.Equal("FAIL: position counts from 1.", await _tool.UpdateBoardColumn("Code review", position: 0, cancellationToken: Ct));
        Assert.Equal(["Backlog", "Ready", "Build", "Code review", "Done"], await LaneNamesAsync());
    }

    [Fact]
    public async Task UpdateBoardColumn_SaysWhenARenameOrMoveChangesWhatTheBoardTreatsAsFinished()
    {
        await _store.EnsureDefaultColumnsAsync(_project, Ct);

        // Done stops being a finished lane when its name no longer says so.
        var reopened = await _tool.UpdateBoardColumn("Done", name: "Released", cancellationToken: Ct);
        Assert.Contains("renamed from \"Done\".", reopened);
        Assert.Contains("The name no longer makes it a finished lane", reopened);
        var closed = await _tool.UpdateBoardColumn("Build", name: "Build complete", cancellationToken: Ct);
        Assert.Contains("The name makes it a finished lane", closed);
        // A rename that leaves the lane's kind alone says nothing about it.
        Assert.DoesNotContain("finished lane", await _tool.UpdateBoardColumn("Ready", name: "Next up", cancellationToken: Ct));

        // The whole order is saved with one lane moved; a place past the end means last.
        var moved = await _tool.UpdateBoardColumn("Backlog", position: 99, cancellationToken: Ct);
        Assert.StartsWith("Moved lane \"Backlog\" to place 5 of 5.", moved);
        Assert.Contains("The board's last lane is now \"Backlog\" (it was \"Released\")", moved);
        Assert.Equal(["Next up", "Build complete", "Review", "Released", "Backlog"], await LaneNamesAsync());

        // A move in the middle leaves the last lane alone; a rename and a move can share one call.
        var both = await _tool.UpdateBoardColumn("Review", name: "Check", position: 1, cancellationToken: Ct);
        Assert.Contains("Updated lane \"Check\"", both);
        Assert.Contains("\nMoved lane \"Check\" to place 1 of 5.", both);
        Assert.DoesNotContain("last lane", both);
        Assert.Equal(["Check", "Next up", "Build complete", "Released", "Backlog"], await LaneNamesAsync());

        // "closed" is not a finished name: it ends desktop-client activity and leaves the totals alone.
        var parked = await _tool.UpdateBoardColumn("Next up", name: "Closed", cancellationToken: Ct);
        Assert.Contains("The name ends desktop-client activity on the lane's cards; they still count as open work.", parked);
        Assert.DoesNotContain("finished lane", parked);
        Assert.Contains("The name no longer ends desktop-client activity on the lane's cards.",
            await _tool.UpdateBoardColumn("Closed", name: "Parked", cancellationToken: Ct));
    }

    // BoardStore.IsCompletedLane (paging, open totals) and DesktopClosedLaneSql (desktop activity)
    // are the conventions these two repeat; a name they disagree on is "closed".
    [Theory]
    [InlineData("Done", true, true)]
    [InlineData("SHIPPED", true, true)]
    [InlineData("Completed work", true, true)]
    [InlineData("Closed", false, true)]
    [InlineData("Review", false, false)]
    [InlineData("NOT DOING", false, false)]
    [InlineData("", false, false)]
    [InlineData(null, false, false)]
    public void LaneNames_FollowTheBoardsConventions(string? name, bool finished, bool endsDesktopActivity)
    {
        Assert.Equal(finished, BoardTool.IsFinishedLaneName(name));
        Assert.Equal(endsDesktopActivity, BoardTool.EndsDesktopActivity(name));
    }

    // Code review of VIBE-342, F1: the store refuses only shared copies, so the tools refuse a
    // dedicated tracker board themselves, whichever way the lane or board is named.
    [Theory]
    [InlineData(false, "Jira")]
    [InlineData(true, "Azure DevOps")]
    public async Task TrackerBoards_RefuseEveryStructureEdit_AndTheLocalBoardBesideThemDoesNot(bool ado, string provider)
    {
        await _store.EnsureDefaultColumnsAsync(_project, Ct);
        var board = await _store.CreateBoardAsync(_project, "Tracker", Ct);
        if (ado)
            await _store.SaveAdoConnectionAsync(AdoConnection(board.Id) with { DedicatedBoard = true }, Ct);
        else
            await _store.SaveJiraConnectionAsync(new BoardJiraConnectionRecord("jira-connection", _project, board.Id, "https://jira.example", "user@example.com",
                true, "saved", null, "project = APP", true, null, null, null, null, null, DedicatedBoard: true), Ct);
        var lanes = await _store.GetColumnsAsync(_project, Ct, board.Id);

        foreach (var response in new[]
        {
            await _tool.CreateBoardColumn("QA", board: board.Id, cancellationToken: Ct),
            await _tool.CreateBoardColumn("QA", board: "Tracker", position: 1, cancellationToken: Ct),
            await _tool.UpdateBoardColumn("Review", name: "Verified", color: "#123456", defaultForNewCards: true, board: board.Id, cancellationToken: Ct),
            // A lane id finds its lane on any board of the project, so the board argument is not what refuses.
            await _tool.UpdateBoardColumn(lanes[0].Id, name: "Verified", cancellationToken: Ct),
            await _tool.UpdateBoardColumn(lanes[0].Id, position: 3, cancellationToken: Ct),
            await _tool.UpdateBoard(board.Id, name: "Changed tracker", displayPrefix: "TRACK", cancellationToken: Ct),
            await _tool.UpdateBoard("Tracker", displayPrefix: "", cancellationToken: Ct)
        })
            Assert.Equal($"FAIL: this board belongs to {provider}. Its lanes and name are changed in the dashboard, not over MCP.", response);

        var after = await _store.GetColumnsAsync(_project, Ct, board.Id);
        Assert.Equal(lanes.Select(lane => (lane.Id, lane.Name, lane.Position, lane.Color, lane.IsDefaultForNewCards)),
            after.Select(lane => (lane.Id, lane.Name, lane.Position, lane.Color, lane.IsDefaultForNewCards)));
        var saved = (await _store.GetBoardAsync(_project, board.Id, Ct))!;
        Assert.Equal(("Tracker", (string?)null), (saved.Name, saved.DisplayPrefix));
        // The project's local board is not a tracker board, so it still takes the change.
        Assert.StartsWith("Created lane \"QA\" (id ", await _tool.CreateBoardColumn("QA", cancellationToken: Ct));
    }

    // Code review of VIBE-342, F2: the reply describes the totals by the store's own rule. "Closed"
    // is not a finished name, so cards in it keep counting; Done to Closed puts them back.
    [Fact]
    public async Task RenamingToClosed_SaysWhatHappensToTheOpenTotals_AndTheTotalsAgree()
    {
        await _store.EnsureDefaultColumnsAsync(_project, Ct);
        var review = (await _service.FindColumnAsync(_project, "Review", Ct))!;
        var done = (await _service.FindColumnAsync(_project, "Done", Ct))!;
        await _store.CreateCardAsync(_project, new(review.Id, "Flagged in review", "", null, "medium", null, [], false) { Flagged = true, Points = 3 }, Ct);
        await _store.CreateCardAsync(_project, new(done.Id, "Flagged when done", "", null, "medium", null, [], false) { Flagged = true, Points = 5 }, Ct);
        var before = await _store.GetCardsPageAsync(_project, new(), Ct);
        Assert.Equal((1, 3L), (before.FlaggedCount, before.RemainingPoints));

        var parked = await _tool.UpdateBoardColumn(review.Id, name: "Closed", cancellationToken: Ct);
        Assert.Contains("they still count as open work", parked);
        Assert.DoesNotContain("leave the open totals", parked);
        var unchanged = await _store.GetCardsPageAsync(_project, new(), Ct);
        Assert.Equal((1, 3L), (unchanged.FlaggedCount, unchanged.RemainingPoints));

        var reopened = await _tool.UpdateBoardColumn(done.Id, name: "Closed out", cancellationToken: Ct);
        Assert.Contains("The name no longer makes it a finished lane: its cards count as open work again.", reopened);
        var counted = await _store.GetCardsPageAsync(_project, new(), Ct);
        Assert.Equal((2, 8L), (counted.FlaggedCount, counted.RemainingPoints));
    }

    // Code review of VIBE-342, F3: another writer changes the lanes between the read and the save
    // of the order. The lane is already created, so the reply says so instead of a bare FAIL that
    // would have the caller create it again.
    [Fact]
    public async Task ARefusedLaneOrder_AfterTheLaneIsSaved_SaysWhatWasSavedAndHowToFinish()
    {
        await _store.EnsureDefaultColumnsAsync(_project, Ct);
        var raced = 0;
        var service = new Mock<IBoardService>(MockBehavior.Strict);
        service.Setup(s => s.GetColumnsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<string?>()))
            .Returns((string project, CancellationToken ct, string? board) => _service.GetColumnsAsync(project, ct, board));
        service.Setup(s => s.FindColumnAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<string?>()))
            .Returns((string project, string lane, CancellationToken ct, string? board) => _service.FindColumnAsync(project, lane, ct, board));
        service.Setup(s => s.CreateColumnAsync(It.IsAny<string>(), It.IsAny<CreateBoardColumnRequest>(), It.IsAny<CancellationToken>()))
            .Returns((string project, CreateBoardColumnRequest request, CancellationToken ct) => _service.CreateColumnAsync(project, request, ct));
        service.Setup(s => s.UpdateColumnAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<UpdateBoardColumnRequest>(), It.IsAny<CancellationToken>()))
            .Returns((string project, string lane, UpdateBoardColumnRequest request, CancellationToken ct) => _service.UpdateColumnAsync(project, lane, request, ct));
        service.Setup(s => s.ReorderColumnsAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>(), It.IsAny<string?>()))
            .Returns(async (string project, IReadOnlyList<string> ids, CancellationToken ct, string? board) =>
            {
                // The first two saves lose the race; later ones go through.
                if (raced++ < 2) await _service.CreateColumnAsync(project, new($"Concurrent addition {raced}", BoardId: board), ct);
                return await _service.ReorderColumnsAsync(project, ids, ct, board);
            });
        var tool = new BoardTool(service.Object, _resolver, _store, new BoardWorkflowService());

        var created = await tool.CreateBoardColumn("QA", position: 1, cancellationToken: Ct);
        var qa = Assert.Single(await _store.GetColumnsAsync(_project, Ct), lane => lane.Name == "QA");
        Assert.StartsWith($"Created lane \"QA\" (id {qa.Id}) as lane 6.", created);
        Assert.EndsWith("\nFAIL: the lane was not moved to place 1: The lane order must list every lane on this board exactly once. "
            + $"The lane exists, so do not create it again; call update_board_column(column=\"{qa.Id}\", position=1) to place it.", created);

        // A rename that saved beside a move that did not: both are said, in that order.
        var renamed = await tool.UpdateBoardColumn(qa.Id, name: "Verify", position: 1, cancellationToken: Ct);
        Assert.StartsWith($"Updated lane \"Verify\" (id {qa.Id}): renamed from \"QA\".", renamed);
        Assert.Contains($"\nFAIL: the lane was not moved to place 1: The lane order must list every lane on this board exactly once. The changes above are saved; call update_board_column(column=\"{qa.Id}\", position=1)", renamed);
        Assert.Equal("Verify", (await _service.FindColumnAsync(_project, qa.Id, Ct))!.Name);

        // Following the reply finishes the job, and nothing was created twice.
        Assert.StartsWith("Moved lane \"Verify\" to place 1 of 8.", await tool.UpdateBoardColumn(qa.Id, position: 1, cancellationToken: Ct));
        var names = await LaneNamesAsync();
        Assert.Equal("Verify", names[0]);
        Assert.Single(names, name => name is "QA" or "Verify");
    }

    [Fact]
    public async Task ARefusedLaneOrder_WithNothingElseSaved_IsAPlainFailure()
    {
        await _store.EnsureDefaultColumnsAsync(_project, Ct);
        var service = new Mock<IBoardService>(MockBehavior.Strict);
        service.Setup(s => s.GetColumnsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<string?>()))
            .Returns((string project, CancellationToken ct, string? board) => _service.GetColumnsAsync(project, ct, board));
        service.Setup(s => s.FindColumnAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<string?>()))
            .Returns((string project, string lane, CancellationToken ct, string? board) => _service.FindColumnAsync(project, lane, ct, board));
        service.Setup(s => s.ReorderColumnsAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>(), It.IsAny<string?>()))
            .Returns(async (string project, IReadOnlyList<string> ids, CancellationToken ct, string? board) =>
            {
                await _service.CreateColumnAsync(project, new("Concurrent addition", BoardId: board), ct);
                return await _service.ReorderColumnsAsync(project, ids, ct, board);
            });
        var tool = new BoardTool(service.Object, _resolver, _store, new BoardWorkflowService());

        Assert.Equal("FAIL: The lane order must list every lane on this board exactly once.",
            await tool.UpdateBoardColumn("Review", position: 1, cancellationToken: Ct));
        Assert.Equal("Backlog", (await LaneNamesAsync())[0]);
    }

    [Fact]
    public async Task CreateBoard_AndUpdateBoard_DoWhatTheDashboardDoes()
    {
        var created = await _tool.CreateBoard("Shop", "shop", Ct);
        var shop = Assert.Single((await _service.GetBoardsAsync(_project, Ct)).Boards, board => board.Name == "Shop");
        Assert.Equal($"Created board \"Shop\" (id {shop.Id}) in {_project} with lanes: Backlog → Ready → Build → Review → Done."
            + $" New cards are numbered SHOP-n.\nPass board={shop.Id} to the other Board tools to work on it.", created);
        Assert.Contains("- Shop (id " + shop.Id, await _tool.ListBoards(Ct));
        Assert.Equal(["Backlog", "Ready", "Build", "Review", "Done"], await LaneNamesAsync(shop.Id));

        // A lane tool given the board works on that board, not the project's first.
        Assert.Contains(" on board Shop at place 1 of 6.", await _tool.CreateBoardColumn("Ideas", board: "Shop", position: 1, cancellationToken: Ct));
        Assert.Equal("Ideas", (await LaneNamesAsync(shop.Id))[0]);
        Assert.DoesNotContain("Ideas", await LaneNamesAsync());

        Assert.Equal($"Updated board \"Storefront\" (id {shop.Id}): renamed from \"Shop\".", await _tool.UpdateBoard(shop.Id, name: "Storefront", cancellationToken: Ct));
        Assert.Equal($"Updated board \"Storefront\" (id {shop.Id}); new cards are numbered WEB-n.", await _tool.UpdateBoard("storefront", displayPrefix: "web", cancellationToken: Ct));
        Assert.Equal("WEB", (await _store.GetBoardAsync(_project, shop.Id, Ct))!.DisplayPrefix);
        // An empty prefix clears the board's own; the reply names the prefix now in effect.
        Assert.Equal($"Updated board \"Storefront\" (id {shop.Id}); new cards are numbered PROJ-n, the repository's default.", await _tool.UpdateBoard("Storefront", displayPrefix: "", cancellationToken: Ct));
        var saved = (await _store.GetBoardAsync(_project, shop.Id, Ct))!;
        Assert.Equal(("Storefront", (string?)null), (saved.Name, saved.DisplayPrefix));

        Assert.Equal("FAIL: a board needs a name.", await _tool.CreateBoard(" ", cancellationToken: Ct));
        Assert.Equal("FAIL: A board needs a name.", await _tool.UpdateBoard(shop.Id, name: "  ", cancellationToken: Ct));
        Assert.Equal("FAIL: nothing to change. Pass name or displayPrefix.", await _tool.UpdateBoard(shop.Id, cancellationToken: Ct));
        Assert.StartsWith("FAIL: board not found: nope.", await _tool.UpdateBoard("nope", name: "Anything", cancellationToken: Ct));
    }

    [Fact]
    public async Task SharedReadOnlyBoard_RefusesLaneAndBoardEdits()
    {
        var board = await _store.ImportSharedBoardAsync(_project,
            new(Guid.NewGuid().ToString("D"), "Shared", "VB", "VB", [new("shared_lane", "Todo", null, 0), new("shared_done", "Done", null, 1)], false, 0),
            "other-account", Ct);

        foreach (var response in new[]
        {
            await _tool.CreateBoardColumn("Forbidden", board: board.Id, cancellationToken: Ct),
            await _tool.UpdateBoardColumn("shared_lane", name: "Forbidden", cancellationToken: Ct),
            await _tool.UpdateBoardColumn("shared_lane", position: 2, cancellationToken: Ct),
            await _tool.UpdateBoard(board.Id, name: "Forbidden", cancellationToken: Ct)
        })
            Assert.Equal("FAIL: " + BoardAccess.ReadOnlyMessage, response);

        Assert.Equal(["Todo", "Done"], await LaneNamesAsync(board.Id));
        Assert.Equal("Shared", (await _store.GetBoardAsync(_project, board.Id, Ct))!.Name);
    }

    [Fact]
    public void StructureTools_AreNotPreApprovedForBoardLaunches()
    {
        // A card agent is granted the read / append / move / link tools without a prompt. Changing the
        // board's lanes or boards is not part of working a card, so these keep the client's own approval.
        foreach (var tool in new[] { "create_board_column", "update_board_column", "create_board", "update_board" })
            Assert.DoesNotContain(tool, BoardMcpAuthorization.ToolNames);
    }

    [Fact]
    public async Task LaneEntries_ReadByName_InListBoardsTheCardAndTheMoveReport()
    {
        var preset = (await _launchService.SavePresetAsync(null, new("Code reviewer", "", "codex", InitialPrompt: "Review"), Ct)).Preset;
        await _tool.CreateBoardCard("Ready for eyes", cancellationToken: Ct);
        var review = (await _service.FindColumnAsync(_project, "Review", Ct))!;
        await _store.SaveLaunchLaneAsync(_project, review.Id, new([new(preset.Id, "preset", "review"), new("preset_gone", "preset")], 0), Ct);

        // The lane line used to print "preset_…" ids; a definition that no longer resolves keeps its id.
        const string lane = "Review (on entry: \"Code reviewer\", \"preset_gone\")";
        Assert.Contains(lane, await _tool.ListBoards(Ct));
        Assert.Contains(lane, await _tool.GetBoardCard("PROJ-1", cancellationToken: Ct));

        var preview = await _tool.MoveBoardCard("PROJ-1", "Review", preview: true, cancellationToken: Ct);
        Assert.Contains("\"Code reviewer\" — ", preview);
        Assert.Contains("id " + preset.Id, preview);
        Assert.DoesNotContain($"\"{preset.Id}\"", preview);

        var moved = await _tool.MoveBoardCard("PROJ-1", "Review", cancellationToken: Ct);
        Assert.Contains("\"Code reviewer\" — ", moved);
        Assert.DoesNotContain($"\"{preset.Id}\"", moved);
        // The card's lane runs name the step too, with the id beside it.
        Assert.Contains($": \"Code reviewer\" ({preset.Id}) · ", await _tool.GetBoardCard("PROJ-1", cancellationToken: Ct));
    }
}
