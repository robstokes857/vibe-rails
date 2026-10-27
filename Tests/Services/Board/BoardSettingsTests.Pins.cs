using Microsoft.Data.Sqlite;
using VibeRails.Services.Board;
using Xunit;

namespace Tests.Services.Board;

public sealed partial class BoardSettingsTests
{
    [Theory]
    [InlineData(false, "comment", false)]
    [InlineData(false, "edit", false)]
    [InlineData(false, "code-link", false)]
    [InlineData(true, "comment", false)]
    [InlineData(true, "edit", false)]
    [InlineData(true, "code-link", false)]
    [InlineData(false, "comment", true)]
    [InlineData(true, "comment", true)]
    [InlineData(false, "create", false)]
    [InlineData(true, "create", false)]
    [InlineData(false, "create", true)]
    [InlineData(true, "create", true)]
    public async Task ActivityPreservesPinnedSlotsInBothAttentionGroups(bool flaggedActivity, string activity, bool legacyPositions)
    {
        var (board, lane, _, _) = await Lanes();
        for (var i = 0; i < 3; i++)
        {
            await _boards.CreateCardAsync(_root, new(lane, "Flagged " + i, "", null, "medium", null, [], false, Flagged: true), Ct);
            await Card(lane);
        }
        var before = (await _boards.GetCardsAsync(_root, Ct, board)).ToArray();
        if (legacyPositions)
        {
            // Older writers can leave physical positions interleaved even though reads group flags first.
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(Ct);
            for (var i = 0; i < before.Length; i++)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "UPDATE BoardCards SET Position=$position WHERE Id=$id;";
                command.Parameters.AddWithValue("$id", before[i].Id);
                command.Parameters.AddWithValue("$position", i < 3 ? i * 2 : (i - 3) * 2 + 1);
                await command.ExecuteNonQueryAsync(Ct);
            }
            before = (await _boards.GetCardsAsync(_root, Ct, board)).ToArray();
        }
        foreach (var pin in new[] { before[1], before[4] })
            Assert.True(await _boards.SetCardPinnedAsync(_root, pin.Id, true, Ct));

        var firstSlot = flaggedActivity ? 0 : 3;
        var touched = before[firstSlot + 2];
        switch (activity)
        {
            case "comment": await _boards.AddCommentAsync(_root, touched.Id, BoardAuthor.User(), "Activity elsewhere", Ct); break;
            case "edit": await _boards.UpdateCardAsync(_root, touched.Id, new(Title: "Edited elsewhere"), Ct); break;
            case "code-link": await _boards.AddCodeLinkAsync(_root, touched.Id, "https://github.com/org/repo/pull/42", Ct); break;
            case "create": touched = await _boards.CreateCardAsync(_root, new(lane, "New card", "", null, "medium", null, [], false, Flagged: flaggedActivity), Ct); break;
        }

        var after = (await _boards.GetCardsAsync(_root, Ct, board)).ToArray();
        var expected = before.Select(card => card.Id).ToList();
        if (activity == "create")
        {
            var oldFirst = expected[firstSlot];
            var oldLast = expected[firstSlot + 2];
            expected[firstSlot] = touched.Id;
            expected[firstSlot + 2] = oldFirst;
            expected.Insert(firstSlot + 3, oldLast);
        }
        else (expected[firstSlot], expected[firstSlot + 2]) = (expected[firstSlot + 2], expected[firstSlot]);
        Assert.Equal(expected, after.Select(card => card.Id));
        foreach (var slot in new[] { 1, 4 })
        {
            var pin = after.Single(card => card.Id == before[slot].Id);
            Assert.True(pin.Pinned);
            Assert.Equal(before[slot].UpdatedUtc, pin.UpdatedUtc);
            Assert.Equal(1, Array.FindIndex(after.Where(card => card.Flagged == pin.Flagged).ToArray(), card => card.Id == pin.Id));
        }
        Assert.Equal(Enumerable.Range(0, after.Length), after.Select(card => card.Position));
        var page = await _boards.GetCardsPageAsync(_root, new(PageSize: 2, ColumnId: lane, Offset: 2), Ct, board);
        Assert.Equal(expected.Skip(2).Take(2), page.Cards.Select(card => card.Id));
    }

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
