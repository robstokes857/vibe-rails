using Xunit;

namespace Tests.Services.Mcp;

public sealed partial class BoardToolClientAuthorTests
{
    [Theory]
    [InlineData("claude-code")]
    [InlineData("codex-mcp-client")]
    [InlineData("ChatGPT")]
    public async Task ProtocolCreationUsesSelectedBoardsDefaultLaneAndHonorsExplicitOverrides(string clientName)
    {
        await _store.EnsureDefaultColumnsAsync(_project, Ct);
        var board = await _service.CreateBoardAsync(_project, new("Another board"), Ct);
        var lanes = await _store.GetColumnsAsync(_project, Ct, board.Id);
        await _store.UpdateColumnAsync(_project, lanes[2].Id, null, null, Ct, true);
        var client = await ConnectAsync(clientName);

        Assert.StartsWith("Created ", await CallAsync(client, "create_board_card", new()
        { ["title"] = "Use the default", ["board"] = board.Id }));
        Assert.StartsWith("Created ", await CallAsync(client, "create_board_card", new()
        { ["title"] = "Explicit lane", ["board"] = board.Id, ["column"] = lanes[0].Name }));

        var cards = await _store.GetCardsAsync(_project, Ct, boardId: board.Id);
        Assert.Equal(lanes[2].Id, Assert.Single(cards, card => card.Title == "Use the default").ColumnId);
        Assert.Equal(lanes[0].Id, Assert.Single(cards, card => card.Title == "Explicit lane").ColumnId);
    }
}
