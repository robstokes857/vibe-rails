using Microsoft.Data.Sqlite;
using VibeRails.Services.Board;
using Xunit;

namespace Tests.Services.Board;

public sealed partial class BoardStoreTests
{
    [Fact]
    public async Task DefaultColumn_PersistsAcrossReopenAndReorder_ExplicitChoiceWins()
    {
        await _store.EnsureDefaultColumnsAsync(_project, Ct);
        var lanes = await _store.GetColumnsAsync(_project, Ct);
        Assert.All(lanes, lane => Assert.False(lane.IsDefaultForNewCards));
        var oldCard = await _store.CreateCardAsync(_project, NewCard("Existing"), Ct);
        var selected = (await _store.UpdateColumnAsync(_project, lanes[2].Id, null, null, Ct, true))!;
        Assert.True(selected.IsDefaultForNewCards);

        var reopened = new BoardStore(_connectionString, _connectionString);
        await reopened.ReorderColumnsAsync(_project, lanes.Reverse().Select(lane => lane.Id).ToArray(), Ct);
        await reopened.UpdateColumnAsync(_project, selected.Id, "Needs refinement", "#64748b", Ct);
        Assert.Equal(selected.Id, Assert.Single(await reopened.GetColumnsAsync(_project, Ct),
            lane => lane.IsDefaultForNewCards).Id);
        Assert.True((await reopened.GetColumnAsync(_project, selected.Id, Ct))!.IsDefaultForNewCards);
        Assert.True((await reopened.GetAllColumnsAsync(_project, Ct)).Single(lane => lane.Id == selected.Id).IsDefaultForNewCards);
        Assert.Equal(selected.Id, (await reopened.CreateCardAsync(_project, NewCard("Default"), Ct)).ColumnId);
        Assert.Equal(lanes[1].Id, (await reopened.CreateCardAsync(_project,
            NewCard("Explicit") with { ColumnId = lanes[1].Id }, Ct)).ColumnId);
        Assert.Equal(oldCard.ColumnId, (await reopened.FindCardAsync(_project, oldCard.Id, Ct))!.ColumnId);
    }

    [Fact]
    public async Task DefaultColumn_IsUniquePerBoard_ScopedAndClearable()
    {
        var first = await _store.CreateBoardAsync(_project, "First", Ct);
        var second = await _store.CreateBoardAsync(_project, "Second", Ct);
        var firstLane = await _store.CreateColumnAsync(_project, "Not doing", "#64748b", Ct, first.Id, isDefaultForNewCards: true);
        var secondLane = await _store.CreateColumnAsync(_project, "Needs refinement", "#64748b", Ct, second.Id, isDefaultForNewCards: true);
        var replacement = await _store.CreateColumnAsync(_project, "Inbox", "#64748b", Ct, first.Id, isDefaultForNewCards: true);
        Assert.False((await _store.GetColumnAsync(_project, firstLane.Id, Ct))!.IsDefaultForNewCards);
        Assert.True((await _store.GetColumnAsync(_project, secondLane.Id, Ct))!.IsDefaultForNewCards);
        // Unchecking another lane must not clear this board's selected lane.
        await _store.UpdateColumnAsync(_project, firstLane.Id, null, null, Ct, false);
        Assert.Null(await _store.UpdateColumnAsync(_otherProject, replacement.Id, null, null, Ct, false));
        Assert.Equal(replacement.Id, (await _store.CreateCardAsync(_project, NewCard("First"), Ct)).ColumnId);
        Assert.Equal(secondLane.Id, (await _store.CreateCardAsync(_project, NewCard("Second") with { BoardId = second.Id }, Ct)).ColumnId);

        await _store.UpdateColumnAsync(_project, replacement.Id, null, null, Ct, false);
        var lanes = await _store.GetColumnsAsync(_project, Ct, first.Id);
        Assert.All(lanes, lane => Assert.False(lane.IsDefaultForNewCards));
        Assert.Equal(lanes[0].Id, (await _store.CreateCardAsync(_project, NewCard("Fallback"), Ct)).ColumnId);
    }

    [Fact]
    public async Task DefaultColumn_UpgradePreservesCards_AndOlderWritersKeepWorking()
    {
        await _store.EnsureDefaultColumnsAsync(_project, Ct);
        var lanes = await _store.GetColumnsAsync(_project, Ct);
        var card = await _store.CreateCardAsync(_project, NewCard("Before upgrade"), Ct);
        // Reproduce the preceding schema in this disposable fixture, with existing data.
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            ALTER TABLE Boards DROP COLUMN DefaultColumnId;
            DELETE FROM SchemaMigrations WHERE Component = 'board-default-column';
            """;
        await command.ExecuteNonQueryAsync(Ct);
        var upgraded = new BoardStore(_connectionString, _connectionString);
        Assert.Equivalent(card, await upgraded.FindCardAsync(_project, card.Id, Ct));
        Assert.All(await upgraded.GetColumnsAsync(_project, Ct), lane => Assert.False(lane.IsDefaultForNewCards));
        await upgraded.UpdateColumnAsync(_project, lanes[2].Id, null, null, Ct, true);

        command.CommandText = """
            UPDATE Boards SET Name = 'Older board writer' WHERE Id = $board;
            UPDATE BoardColumns SET Name = 'Older lane writer' WHERE Id = $lane;
            """;
        command.Parameters.AddWithValue("$board", lanes[2].BoardId);
        command.Parameters.AddWithValue("$lane", lanes[2].Id);
        await command.ExecuteNonQueryAsync(Ct);
        Assert.Equal(lanes[2].Id, (await upgraded.CreateCardAsync(_project, NewCard("After old write"), Ct)).ColumnId);
        // A legacy deletion also clears the reference through the nullable foreign key.
        var emptyDefault = await upgraded.CreateColumnAsync(_project, "Empty default", "#64748b", Ct, isDefaultForNewCards: true);
        command.CommandText = "PRAGMA foreign_keys = ON; DELETE FROM BoardColumns WHERE Id = $empty;";
        command.Parameters.Clear();
        command.Parameters.AddWithValue("$empty", emptyDefault.Id);
        await command.ExecuteNonQueryAsync(Ct);
        Assert.Equal(lanes[0].Id, (await upgraded.CreateCardAsync(_project, NewCard("After deletion"), Ct)).ColumnId);
        command.CommandText = "PRAGMA foreign_key_check;";
        Assert.Null(await command.ExecuteScalarAsync(Ct));
    }

    [Fact]
    public async Task DefaultColumn_DeletingSelectedLaneRestoresFirstLane_AndChangesPollStamp()
    {
        await _store.EnsureDefaultColumnsAsync(_project, Ct);
        var lanes = await _store.GetColumnsAsync(_project, Ct);
        var before = await _store.GetChangeStampAsync(Ct);
        await _store.UpdateColumnAsync(_project, lanes[2].Id, null, null, Ct, true);
        Assert.NotEqual(before, await _store.GetChangeStampAsync(Ct));
        await _store.DeleteColumnAsync(_project, lanes[2].Id, Ct);
        Assert.Equal(lanes[0].Id, (await _store.CreateCardAsync(_project, NewCard("After deletion"), Ct)).ColumnId);
    }
}
