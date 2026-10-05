using Tests.Services.BertV2;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Data.Sqlite;
using VibeRails.DTOs;
using VibeRails.Services.Board;
using VibeRails.Services.Mcp.Tools;
using Xunit;

namespace Tests.Services.Mcp;

public sealed partial class BoardToolTests
{
    [Fact]
    public async Task ForeignLegacyCardsUseRowIdentityInRecallAndLinkedNavigation()
    {
        var local = await _service.CreateCardAsync(_project, new CreateBoardCardRequest(Title: "Local"), Ct);
        var elsewhere = _project + "-other";
        var foreign = await _service.CreateCardAsync(elsewhere, new CreateBoardCardRequest(Title: "uniqueforeignlegacy"), Ct);
        await using (var connection = new SqliteConnection(_connectionString))
        {
            await connection.OpenAsync(Ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE BoardCards SET CardKey=NULL WHERE Id=$id";
            command.Parameters.AddWithValue("$id", foreign.Id);
            await command.ExecuteNonQueryAsync(Ct);
        }
        var index = SearchTestIndex.Build(_store, _project);
        var recall = new BoardRecallService(_resolver, index, new BoardSearchService(index,
            () => throw new IOException("Model unavailable"), NullLogger<BoardSearchService>.Instance));
        var text = (await recall.SearchAsync("uniqueforeignlegacy", 5, Ct)).Text;
        Assert.Contains($"More: get_board_card(card: \"{foreign.Id}\")", text);
        await _service.LinkCardAsync(_project, local.Id, foreign.Id, Ct);
        var linked = await _tool.GetBoardCard(local.Id, cancellationToken: Ct);
        Assert.Contains($"id {foreign.Id}", linked);
        Assert.Contains("another repository: " + elsewhere, linked);
        Assert.Contains("passing its row ID", linked);
    }

    [Fact]
    public async Task CardSearchSharesGlobalDiscussionDiscoveryAndDoesNotAttachSession()
    {
        var local = await _service.CreateCardAsync(_project, new CreateBoardCardRequest(Title: "Local retrieval"), Ct);
        var elsewhere = _project + "-other";
        var foreign = await _service.CreateCardAsync(elsewhere, new CreateBoardCardRequest(Title: "Foreign retrieval"), Ct);
        await _service.AddCommentAsync(elsewhere, foreign.Id, BoardAuthor.User(), "uniquesearchdiscussion", Ct);
        var search = new BoardSearchService(SearchTestIndex.Build(_store, _project), () => throw new IOException("Model unavailable"), NullLogger<BoardSearchService>.Instance);
        var tool = new BoardTool(_service, _resolver, _store, search: search);
        _resolver.CurrentSessionId = "search-only";
        var results = await tool.SearchBoardCards("retrieval", cancellationToken: Ct);
        Assert.True(results.IndexOf(local.Id, StringComparison.Ordinal) < results.IndexOf(foreign.Id, StringComparison.Ordinal));
        Assert.Contains("WARNING: another repository", results);
        Assert.Contains(elsewhere, results);
        Assert.Contains(foreign.Id, await tool.SearchBoardCards("uniquesearchdiscussion", cancellationToken: Ct));
        Assert.Contains(foreign.Id, await tool.SearchBoardCards(foreign.Key, cancellationToken: Ct));
        Assert.StartsWith("FAIL:", await tool.SearchBoardCards(" ", cancellationToken: Ct));
        Assert.StartsWith("FAIL:", await tool.SearchBoardCards(new string('x', 1001), cancellationToken: Ct));
        Assert.Null(await _store.FindSessionLinkAsync("search-only", Ct));
    }
}
