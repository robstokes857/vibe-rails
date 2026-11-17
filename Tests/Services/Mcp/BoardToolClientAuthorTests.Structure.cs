using VibeRails.Services.Board;
using Xunit;

namespace Tests.Services.Mcp;

/// <summary>
/// The lane and board tools (VIBE-342) over the real protocol: they are listed, a client that
/// names itself can use them, and one that does not is refused before anything is written.
/// </summary>
public sealed partial class BoardToolClientAuthorTests
{
    [Fact]
    public async Task StructureTools_RefuseAClientWithoutAName_AndNothingChanges()
    {
        await _store.EnsureDefaultColumnsAsync(_project, Ct);
        var before = (await _store.GetColumnsAsync(_project, Ct)).Select(lane => (lane.Id, lane.Name, lane.Position)).ToList();
        var board = Assert.Single(await _store.GetBoardsAsync(_project, Ct));
        var client = await ConnectAsync("Agent");

        foreach (var (tool, arguments) in new (string, Dictionary<string, object?>)[]
                 {
                     ("create_board_column", new() { ["name"] = "Anonymous" }),
                     ("update_board_column", new() { ["column"] = "Review", ["name"] = "Anonymous" }),
                     ("update_board_column", new() { ["column"] = "Review", ["position"] = 1 }),
                     ("create_board", new() { ["name"] = "Anonymous" }),
                     ("update_board", new() { ["board"] = board.Id, ["name"] = "Anonymous" }),
                 })
            Assert.StartsWith("FAIL: this MCP client did not say which agent it is, so nothing was written.", await CallAsync(client, tool, arguments));

        Assert.Equal(before, (await _store.GetColumnsAsync(_project, Ct)).Select(lane => (lane.Id, lane.Name, lane.Position)).ToList());
        Assert.Equal(board.Name, Assert.Single(await _store.GetBoardsAsync(_project, Ct)).Name);
    }

    [Fact]
    public async Task StructureTools_RefuseATrackerBoard_ForANamedClient()
    {
        await _store.EnsureDefaultColumnsAsync(_project, Ct);
        var board = await _store.CreateBoardAsync(_project, "Tracker", Ct);
        await _store.SaveJiraConnectionAsync(new BoardJiraConnectionRecord("jira-connection", _project, board.Id, "https://jira.example", "user@example.com",
            true, "saved", null, "project = APP", true, null, null, null, null, null, DedicatedBoard: true), Ct);
        var lanes = (await _store.GetColumnsAsync(_project, Ct, board.Id)).Select(lane => (lane.Id, lane.Name, lane.Position)).ToList();
        var client = await ConnectAsync("claude-code");

        foreach (var (tool, arguments) in new (string, Dictionary<string, object?>)[]
                 {
                     ("create_board_column", new() { ["name"] = "QA", ["board"] = board.Id }),
                     ("update_board_column", new() { ["column"] = lanes[0].Id, ["name"] = "Verified" }),
                     ("update_board_column", new() { ["column"] = lanes[0].Id, ["position"] = 2 }),
                     ("update_board", new() { ["board"] = board.Id, ["name"] = "Changed" }),
                 })
            Assert.Equal("FAIL: this board belongs to Jira. Its lanes and name are changed in the dashboard, not over MCP.", await CallAsync(client, tool, arguments));

        Assert.Equal(lanes, (await _store.GetColumnsAsync(_project, Ct, board.Id)).Select(lane => (lane.Id, lane.Name, lane.Position)).ToList());
        Assert.Equal("Tracker", (await _store.GetBoardAsync(_project, board.Id, Ct))!.Name);
    }

    [Fact]
    public async Task StructureTools_WorkForANamedClient()
    {
        await _store.EnsureDefaultColumnsAsync(_project, Ct);
        var client = await ConnectAsync("claude-code");

        Assert.StartsWith("Created lane \"QA\" (id ", await CallAsync(client, "create_board_column", new() { ["name"] = "QA", ["position"] = 4 }));
        Assert.StartsWith("Updated lane \"Verify\" (id ", await CallAsync(client, "update_board_column", new() { ["column"] = "QA", ["name"] = "Verify" }));
        Assert.Equal(["Backlog", "Ready", "Build", "Verify", "Review", "Done"],
            (await _store.GetColumnsAsync(_project, Ct)).OrderBy(lane => lane.Position).Select(lane => lane.Name));

        Assert.StartsWith("Created board \"Shop\" (id ", await CallAsync(client, "create_board", new() { ["name"] = "Shop" }));
        Assert.StartsWith("Updated board \"Storefront\" (id ", await CallAsync(client, "update_board", new() { ["board"] = "Shop", ["name"] = "Storefront" }));
        Assert.Contains(await _store.GetBoardsAsync(_project, Ct), saved => saved.Name == "Storefront");
    }
}
