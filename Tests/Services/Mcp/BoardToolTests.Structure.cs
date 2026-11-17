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
        Assert.Contains("Its name makes it a finished lane", finished);
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
        Assert.Contains("The new name no longer makes it a finished lane", reopened);
        var closed = await _tool.UpdateBoardColumn("Build", name: "Build complete", cancellationToken: Ct);
        Assert.Contains("The new name makes it a finished lane", closed);
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
    }

    [Theory]
    [InlineData("Done", true)]
    [InlineData("SHIPPED", true)]
    [InlineData("Completed work", true)]
    [InlineData("Closed", true)]
    [InlineData("Review", false)]
    [InlineData("NOT DOING", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsFinishedLaneName_FollowsTheBoardsConvention(string? name, bool finished) =>
        Assert.Equal(finished, BoardTool.IsFinishedLaneName(name));

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
