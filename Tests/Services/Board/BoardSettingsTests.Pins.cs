using VibeRails.Services.Board;
using Xunit;

namespace Tests.Services.Board;

public sealed partial class BoardSettingsTests
{
    [Fact]
    public async Task PinnedSlotsSurviveEditsCommentsAndOtherCardsBeingPromoted()
    {
        var (board, lane, otherLane, _) = await Lanes();
        for (var i = 0; i < 5; i++) await Card(lane);
        var ordered = (await _boards.GetCardsAsync(_root, Ct, board)).ToList();
        var first = ordered[0];
        var middle = ordered[2];
        Assert.True(await _boards.SetCardPinnedAsync(_root, first.Id, true, Ct));
        Assert.True(await _boards.SetCardPinnedAsync(_root, middle.Id, true, Ct));
        var updated = await _boards.UpdateCardAsync(_root, middle.Id, new(Title: "Pinned edit"), Ct);
        Assert.Equal(2, updated!.Position);
        await _boards.AddCommentAsync(_root, middle.Id, BoardAuthor.User(), "Still here", Ct);
        await _boards.AddCommentAsync(_root, ordered[4].Id, BoardAuthor.User(), "Move me up", Ct);
        var after = (await _boards.GetCardsAsync(_root, Ct, board)).ToList();
        Assert.Equal(first.Id, after[0].Id);
        Assert.Equal(middle.Id, after[2].Id);
        Assert.Equal(ordered[4].Id, after[1].Id);
        Assert.Equal(Enumerable.Range(0, 5), after.Select(card => card.Position));
        // A new card takes an unpinned slot, rather than shifting the pins.
        await Card(lane);
        after = (await _boards.GetCardsAsync(_root, Ct, board)).ToList();
        Assert.Equal(first.Id, after[0].Id);
        Assert.Equal(middle.Id, after[2].Id);
        var reopened = new BoardStore(_connectionString, _stateConnectionString);
        Assert.True((await reopened.FindCardAsync(_root, middle.Id, Ct))!.Pinned);
        Assert.False(await _boards.SetCardPinnedAsync(_root + "-foreign", middle.Id, false, Ct));
        Assert.True(await _boards.SetCardPinnedAsync(_root, middle.Id, false, Ct));
        await _boards.AddCommentAsync(_root, middle.Id, BoardAuthor.User(), "Unpinned", Ct);
        Assert.Equal(middle.Id, (await _boards.GetCardsAsync(_root, Ct, board))[1].Id);
        // Explicit moves still work and retain the pin.
        await _boards.MoveCardAsync(_root, first.Id, otherLane, 0, Ct);
        var moved = (await _boards.FindCardAsync(_root, first.Id, Ct))!;
        Assert.Equal(otherLane, moved.ColumnId);
        Assert.True(moved.Pinned);
        // The editor's lane save uses UpdateCard rather than the drag endpoint.
        await _boards.UpdateCardAsync(_root, first.Id, new(ColumnId: lane), Ct);
        after = (await _boards.GetCardsAsync(_root, Ct, board)).Where(card => card.ColumnId == lane).ToList();
        Assert.Equal(first.Id, after[0].Id);
        Assert.Equal(Enumerable.Range(0, after.Count), after.Select(card => card.Position));
    }
}
