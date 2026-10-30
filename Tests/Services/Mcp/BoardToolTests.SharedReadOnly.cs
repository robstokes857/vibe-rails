using VibeRails.Services.Board;
using Xunit;

namespace Tests.Services.Mcp;

public sealed partial class BoardToolTests
{
    [Fact]
    public async Task SharedBoardToolsCanReadButCannotCreateEditCommentOrMove()
    {
        var board = await _store.ImportSharedBoardAsync(_project,
            new(Guid.NewGuid().ToString("D"), "Shared", "VB", "VB", [new("shared_lane", "Todo", null, 0)], false, 0),
            "other-account", Ct);
        var card = (await _store.CreateSyncedCardAsync(_project, "shared_card", "VB-QWERT-1",
            new("shared_lane", "Shared task", "Keep", null, "medium", null, [], false, BoardId: board.Id),
            BoardAuthor.User(), new("shared_created", 1, DateTime.UtcNow, BoardId: board.Id), Ct))!;

        Assert.Contains("Shared task", await _tool.GetBoardCard(card.Key, cancellationToken: Ct));
        foreach (var response in new[]
        {
            await _tool.CreateBoardCard("Forbidden", board: board.Id, cancellationToken: Ct),
            await _tool.UpdateBoardCard(card.Key, title: "Forbidden", cancellationToken: Ct),
            await _tool.AddBoardComment("Forbidden", card.Key, Ct),
            await _tool.MoveBoardCard(card.Key, "shared_lane", cancellationToken: Ct)
        })
        {
            Assert.StartsWith("FAIL:", response);
            Assert.Contains(BoardAccess.ReadOnlyMessage, response);
        }
        Assert.Equal("Shared task", (await _store.FindCardAsync(_project, card.Id, Ct))!.Title);
        Assert.Empty((await _store.GetCardDetailAsync(_project, card.Id, Ct))!.Comments);
        Assert.Equal(0, await _store.CountUnsentLogEntriesAsync(board.Id, Ct));
    }
}
