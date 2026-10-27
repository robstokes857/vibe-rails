using Microsoft.Data.Sqlite;
using Xunit;

namespace Tests.Services.Mcp;

public sealed partial class BoardToolTests
{
    [Fact]
    public async Task GhostGuidanceAndFilteringWorkWithoutReadingRepositoryInstructions()
    {
        await _tool.CreateBoardCard("Missing code", cancellationToken: Ct);
        await _tool.CreateBoardCard("Normal card", cancellationToken: Ct);
        var card = (await _service.FindCardAsync(_project, "PROJ-1", Ct))!;
        await using (var connection = new SqliteConnection(_connectionString))
        {
            await connection.OpenAsync(Ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE BoardCards SET GhostReason='no-code' WHERE Id=$id;";
            command.Parameters.AddWithValue("$id", card.Id);
            await command.ExecuteNonQueryAsync(Ct);
        }
        var ghosts = await _tool.ListBoardCards(ghost: true, cancellationToken: Ct);
        Assert.Contains("Missing code", ghosts);
        Assert.DoesNotContain("Normal card", ghosts);
        Assert.Contains("recheck_board_ghost", ghosts);
        Assert.Contains("clearGhost=true", ghosts);
        var nonGhosts = await _tool.ListBoardCards(ghost: false, cancellationToken: Ct);
        Assert.Contains("Normal card", nonGhosts);
        Assert.DoesNotContain("Missing code", nonGhosts);
        Assert.Contains("Local, unpushed", await _tool.GetBoardCard(card.Key, cancellationToken: Ct));
        Assert.Contains("Ghost dismissed", await _tool.UpdateBoardCard(card.Key, clearGhost: true, cancellationToken: Ct));
        Assert.Null((await _service.FindCardAsync(_project, card.Id, Ct))!.GhostReason);
        Assert.Equal("No cards match.", await _tool.ListBoardCards(ghost: true, cancellationToken: Ct));
        Assert.Contains("unavailable", await _tool.RecheckBoardGhost(card.Key, cancellationToken: Ct));
    }
}
