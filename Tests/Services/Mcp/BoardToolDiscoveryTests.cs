using Microsoft.Data.Sqlite;
using VibeRails.DTOs;
using VibeRails.Services.Board;
using Xunit;

namespace Tests.Services.Mcp;

public sealed partial class BoardToolTests
{
    [Fact]
    public async Task LocalDiscovery_ListsCurrentProjectFirst_AndLabelsOtherProjects()
    {
        var elsewhere = BoardPaths.NormalizeProjectPath(Path.Combine(_root, "aaa-other"));
        var foreign = await _service.CreateBoardAsync(elsewhere, new CreateBoardRequest("Other board"), Ct);
        await _service.CreateCardAsync(elsewhere, new CreateBoardCardRequest(Title: "Elsewhere", BoardId: foreign.Id), Ct);
        var local = await _service.CreateBoardAsync(_project, new CreateBoardRequest("Local board"), Ct);
        var empty = await _service.CreateBoardAsync(elsewhere, new CreateBoardRequest("Empty board"), Ct);

        var result = await _tool.ListBoards(Ct);

        Assert.StartsWith($"Current project boards for {_project}:\n", result);
        var split = result.IndexOf("Other local boards (outside the current project):", StringComparison.Ordinal);
        Assert.True(result.IndexOf(local.Id, StringComparison.Ordinal) < split);
        Assert.True(result.IndexOf(foreign.Id, StringComparison.Ordinal) > split);
        Assert.Contains($"Project: {elsewhere}", result);
        Assert.Contains($"Other board (id {foreign.Id}, 1 card; lanes:", result);
        Assert.Contains($"Empty board (id {empty.Id}, 0 cards; lanes:", result);
        Assert.Contains("; current)", result[..split]);
        Assert.DoesNotContain("; current)", result[split..]);
        Assert.Contains("board=<id>", result);
        Assert.Equal(3, (await _store.GetLocalBoardsAsync(Ct)).Count);
        Assert.Null(await _store.FindSessionLinkAsync("unlinked", Ct));
    }

    [Fact]
    public async Task OtherLocalBoard_CanCreateReadEditMoveCommentAndAttachFiles_WithoutChangingDefaults()
    {
        await _tool.CreateBoardCard("Local", cancellationToken: Ct);
        var local = (await _store.FindCardAsync(_project, "PROJ-1", Ct))!;
        _resolver.CurrentSessionId = "sess-launch";
        await _store.LinkSessionAsync(_project, local.Id, "sess-launch", null, "base:codex", "codex", "Codex", BoardSessionRecord.LaunchOrigin, Ct);
        var elsewhere = BoardPaths.NormalizeProjectPath(Path.Combine(_root, "other"));
        var board = await _service.CreateBoardAsync(elsewhere, new CreateBoardRequest("Elsewhere"), Ct);

        Assert.Contains($"Board lanes for {elsewhere} (board Elsewhere)", await _tool.ListBoardColumns(board.Id, Ct));
        Assert.Contains($"Other local project: {elsewhere}", await _tool.CreateBoardCard("Remote task", board: board.Id, column: "Build", cancellationToken: Ct));
        var card = Assert.Single((await _service.GetCardsAsync(elsewhere, Ct, board.Id)).Cards);
        var listing = await _tool.ListBoardCards(board: board.Id, cancellationToken: Ct);
        Assert.Contains(card.Key, listing);
        Assert.Contains($"— id {card.Id}", listing);
        Assert.Contains(elsewhere, listing);
        Assert.DoesNotContain(local.Key, listing);
        Assert.Contains($"Project: {elsewhere}", await _tool.GetBoardCard(card.Key.ToLowerInvariant(), cancellationToken: Ct));
        Assert.StartsWith("Updated ", await _tool.UpdateBoardCard(card.Id, title: "Edited", cancellationToken: Ct));
        Assert.StartsWith("Comment ", await _tool.AddBoardComment("Other project progress", card.Key, Ct));
        Assert.StartsWith("Attached ", await _tool.AddBoardAttachment("notes.txt", "Evidence", card.Key, Ct));
        var detail = (await _service.GetCardAsync(elsewhere, card.Id, Ct))!;
        var attachment = Assert.Single(detail.Attachments);
        var read = await _tool.ReadBoardAttachment(attachment.Id, card.Key, cancellationToken: Ct);
        Assert.NotEqual(true, read.IsError);
        Assert.Contains("Moved ", await _tool.MoveBoardCard(card.Key, "Review", cancellationToken: Ct));
        Assert.Equal("Edited", detail.Title);
        Assert.Contains(detail.Comments, c => c.Body == "Other project progress");
        Assert.Empty(detail.Sessions);
        Assert.StartsWith("FAIL: session attachments must stay", await _tool.AttachBoardSession(card.Key, Ct));
        Assert.Equal(local.Id, (await _store.FindSessionLinkAsync("sess-launch", Ct))!.CardId);
        Assert.StartsWith(local.Key + ": Local", await _tool.GetBoardCard(cancellationToken: Ct));
        Assert.DoesNotContain("Edited", await _tool.ListBoardCards(cancellationToken: Ct));
        Assert.Contains("Local sibling", await _tool.CreateBoardCard("Local sibling", cancellationToken: Ct));
        Assert.Equal(2, (await _store.GetCardsAsync(_project, Ct)).Count);
        Assert.Null(await _store.FindCardAsync(_project, card.Id, Ct));
        Assert.Null(await _service.GetCardAsync(_project, card.Key, Ct));

        // Commit capture stays in the caller's checkout; only the selected project's
        // attachments participate, and a cross-project move or file read is still rejected.
        Assert.StartsWith("Linked ", await _tool.LinkBoardCommit("abc1234", card.Key, Ct));
        Assert.Single((await _store.GetCardDetailAsync(elsewhere, card.Id, Ct))!.Commits);
        Assert.Empty((await _store.GetCardDetailAsync(_project, local.Id, Ct))!.Commits);
        Assert.StartsWith("FAIL:", await _tool.MoveBoardCard(card.Key, local.ColumnId, cancellationToken: Ct));
        Assert.True((await _tool.ReadBoardAttachment(attachment.Id, local.Key, cancellationToken: Ct)).IsError);
    }

    [Fact]
    public async Task OtherProjectWrite_FromUnlinkedSession_DoesNotSetAnOmittedTarget()
    {
        _resolver.CurrentSessionId = "unlinked";
        var elsewhere = BoardPaths.NormalizeProjectPath(Path.Combine(_root, "other"));
        var board = await _service.CreateBoardAsync(elsewhere, new CreateBoardRequest("Other"), Ct);
        Assert.StartsWith("Created ", await _tool.CreateBoardCard("Foreign", board: "other", cancellationToken: Ct));
        var card = Assert.Single((await _store.GetCardsAsync(elsewhere, Ct, board.Id)));
        Assert.StartsWith("Comment ", await _tool.AddBoardComment("Hello", card.Key, Ct));
        Assert.Null(await _store.FindSessionLinkAsync("unlinked", Ct));
        Assert.StartsWith("FAIL: no card given", await _tool.GetBoardCard(cancellationToken: Ct));
        Assert.StartsWith("Created ", await _tool.CreateBoardCard("Current project", cancellationToken: Ct));
        Assert.Equal(_project, (await _store.FindSessionLinkAsync("unlinked", Ct))!.ProjectPath);
    }

    [Fact]
    public async Task BoardNames_PreferTheCurrentProject_AreAmbiguousOnlyAmongOtherProjects_AndExactIdsWin()
    {
        var local = await _service.CreateBoardAsync(_project, new CreateBoardRequest("Sprint"), Ct);
        var elsewhere = BoardPaths.NormalizeProjectPath(Path.Combine(_root, "other"));
        var foreign = await _service.CreateBoardAsync(elsewhere, new CreateBoardRequest("sPRINT"), Ct);
        // A name unique in the caller's project resolves there, even though another project shares it.
        Assert.StartsWith("Created ", await _tool.CreateBoardCard("Local sprint", board: "SPRINT", cancellationToken: Ct));
        Assert.Equal("Local sprint", Assert.Single(await _store.GetCardsAsync(_project, Ct, local.Id)).Title);
        Assert.Empty(await _store.GetCardsAsync(elsewhere, Ct, foreign.Id));

        // Other projects are still searched: a name only they have resolves when it is unique...
        var release = await _service.CreateBoardAsync(elsewhere, new CreateBoardRequest("Release"), Ct);
        Assert.Contains($"Other local project: {elsewhere}", await _tool.CreateBoardCard("Foreign release", board: "release", cancellationToken: Ct));
        Assert.Equal("Foreign release", Assert.Single(await _store.GetCardsAsync(elsewhere, Ct, release.Id)).Title);
        // ...and is ambiguous when two other projects share it.
        var third = BoardPaths.NormalizeProjectPath(Path.Combine(_root, "third"));
        var thirdRelease = await _service.CreateBoardAsync(third, new CreateBoardRequest("RELEASE"), Ct);
        Assert.Contains("ambiguous", await _tool.CreateBoardCard("Wrong", board: "Release", cancellationToken: Ct));
        Assert.Contains("Use a board ID from list_boards", await _tool.ListBoardColumns("release", Ct));
        Assert.Empty(await _store.GetCardsAsync(third, Ct, thirdRelease.Id));

        await _service.CreateBoardAsync(_project, new CreateBoardRequest(foreign.Id), Ct);
        Assert.StartsWith("Created ", await _tool.CreateBoardCard("Exact ID", board: foreign.Id, cancellationToken: Ct));
        Assert.Equal("Exact ID", Assert.Single(await _store.GetCardsAsync(elsewhere, Ct, foreign.Id)).Title);
    }

    [Fact]
    public async Task CardAliasesStayCurrent_AndDeletedForeignCardsCannotBeReadOrWritten()
    {
        await _tool.CreateBoardCard("Local", cancellationToken: Ct);
        var local = (await _store.FindCardAsync(_project, "PROJ-1", Ct))!;
        var elsewhere = BoardPaths.NormalizeProjectPath(Path.Combine(_root, "other"));
        var foreign = await _service.CreateCardAsync(elsewhere, new CreateBoardCardRequest(Title: "Foreign", DisplayId: "PROJ-1"), Ct);
        Assert.StartsWith(local.Key + ": Local", await _tool.GetBoardCard("PROJ-1", cancellationToken: Ct));
        Assert.StartsWith(foreign.Key + ": Foreign", await _tool.GetBoardCard(foreign.Key, cancellationToken: Ct));
        Assert.StartsWith("FAIL: card not found", await _tool.GetBoardCard("OTHER-1", cancellationToken: Ct));
        Assert.Null(await _store.FindLocalCardAsync("PROJ-1", Ct));
        Assert.True(await _store.DeleteCardAsync(elsewhere, foreign.Id, Ct));
        Assert.StartsWith("FAIL: card not found", await _tool.GetBoardCard(foreign.Key, cancellationToken: Ct));
        Assert.StartsWith("FAIL: card not found", await _tool.UpdateBoardCard(foreign.Id, title: "Revive", cancellationToken: Ct));
    }

    [Fact]
    public async Task ExplicitBoard_RejectsLanesOnAnotherBoardOrProject()
    {
        var local = await _service.CreateBoardAsync(_project, new CreateBoardRequest("Local"), Ct);
        var elsewhere = BoardPaths.NormalizeProjectPath(Path.Combine(_root, "other"));
        var first = await _service.CreateBoardAsync(elsewhere, new CreateBoardRequest("First"), Ct);
        var second = await _service.CreateBoardAsync(elsewhere, new CreateBoardRequest("Second"), Ct);
        foreach (var lane in new[] { local.Columns[0].Id, second.Columns[0].Id })
            Assert.StartsWith("FAIL: lane not found", await _tool.CreateBoardCard("Wrong", board: first.Id, column: lane, cancellationToken: Ct));
        Assert.Empty(await _store.GetCardsAsync(elsewhere, Ct, first.Id));
        Assert.Empty(await _store.GetCardsAsync(elsewhere, Ct, second.Id));
        var emptyListing = await _tool.ListBoardCards(board: first.Id, cancellationToken: Ct);
        Assert.Contains($"Other local project: {elsewhere}", emptyListing);
        Assert.Contains("No cards match.", emptyListing);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("PROJ-1")]
    public async Task LegacyForeignCards_UseRowIds_WithoutCapturingLocalAliases(string? storedKey)
    {
        await _tool.CreateBoardCard("Local", cancellationToken: Ct);
        var local = (await _store.FindCardAsync(_project, "PROJ-1", Ct))!;
        var elsewhere = BoardPaths.NormalizeProjectPath(Path.Combine(_root, "other"));
        var foreign = await _service.CreateCardAsync(elsewhere, new CreateBoardCardRequest(Title: "Legacy"), Ct);
        // Model an older writer's NULL key and an imported shared board's stored short key.
        await using (var connection = new SqliteConnection(_connectionString))
        {
            await connection.OpenAsync(Ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE BoardCards SET CardKey = $key WHERE Id = $id;";
            command.Parameters.AddWithValue("$key", (object?)storedKey ?? DBNull.Value);
            command.Parameters.AddWithValue("$id", foreign.Id);
            await command.ExecuteNonQueryAsync(Ct);
        }

        Assert.Null(await _store.FindLocalCardAsync("PROJ-1", Ct));
        Assert.StartsWith(local.Key + ": Local", await _tool.GetBoardCard("PROJ-1", cancellationToken: Ct));
        Assert.Contains($"Card ID: {foreign.Id}", await _tool.GetBoardCard(foreign.Id, cancellationToken: Ct));
        Assert.StartsWith("Updated ", await _tool.UpdateBoardCard(foreign.Id, title: "Edited legacy", cancellationToken: Ct));
        Assert.Equal("Local", (await _store.FindCardAsync(_project, local.Id, Ct))!.Title);
        Assert.Contains($"— id {foreign.Id}", await _tool.ListBoardCards(board: foreign.BoardId, cancellationToken: Ct));
    }
}
