using Xunit;

namespace Tests.Services.Mcp;

public sealed partial class BoardToolTests
{
    [Fact]
    public async Task CreateBoardCard_UsesDefaultColumn_UnlessAnExplicitLaneIsChosen()
    {
        await _store.EnsureDefaultColumnsAsync(_project, Ct);
        var lanes = await _store.GetColumnsAsync(_project, Ct);
        await _store.UpdateColumnAsync(_project, lanes[2].Id, null, null, Ct, true);
        Assert.StartsWith("Created ", await _tool.CreateBoardCard("Default destination", cancellationToken: Ct));
        Assert.StartsWith("Created ", await _tool.CreateBoardCard("Explicit destination", column: lanes[0].Id, cancellationToken: Ct));
        var cards = await _store.GetCardsAsync(_project, Ct);
        Assert.Equal(lanes[2].Id, cards.Single(card => card.Title == "Default destination").ColumnId);
        Assert.Equal(lanes[0].Id, cards.Single(card => card.Title == "Explicit destination").ColumnId);
    }
}
