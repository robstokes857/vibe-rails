using Moq;
using VibeRails.Services.AzureDevOps;
using VibeRails.Services.Board;
using VibeRails.Services.Mcp.Tools;
using VibeRails.Services.Terminal;
using Xunit;

namespace Tests.Services.Mcp;

/// <summary>add_ado_comment, syncToRemote and the Azure DevOps card text (VIBE-168), mirroring the Jira tool tests.</summary>
public sealed partial class BoardToolTests
{
    private const string AdoConnectionId = "ado_cccccccccccc";

    [Fact]
    public async Task SyncToRemoteKeepsInternalNotesOffAzureDevOps_AndSyncToJiraIsAnAlias()
    {
        var (card, _, _, _) = await AdoFixtureAsync();
        var tool = new BoardTool(_service, _resolver, _store, new BoardWorkflowService(_store, new BoardReviewService(_store, _service)));
        await tool.AddBoardComment("Shared update", card.Key, Ct);
        Assert.EndsWith(" Not sent to Jira or Azure DevOps.", await tool.AddBoardComment("Internal note", card.Key, Ct, syncToRemote: false));
        await tool.AppendBoardNote("Older client note", card.Key, Ct, syncToJira: false);
        // syncToRemote wins over the alias when both are given.
        await tool.AddBoardComment("Remote wins", card.Key, Ct, syncToJira: false, syncToRemote: true);
        await tool.AppendBoardNote("Remote wins off", card.Key, Ct, syncToJira: true, syncToRemote: false);

        var pending = await _store.GetPendingAdoDeliveriesAsync(Ct);
        Assert.Equal(2, pending.Count);
        var detail = (await _service.GetCardAsync(_project, card.Id, Ct))!;
        Assert.Equal(["Internal note", "Older client note", "Remote wins off"],
            detail.Comments.Where(c => !c.SyncToJira).Select(c => c.Body).OrderBy(b => b, StringComparer.Ordinal).ToArray());

        var text = await tool.GetBoardCard(card.Key, cancellationToken: Ct);
        Assert.Contains("Azure DevOps work item: #77 https://dev.azure.com/contoso/Web/_workitems/edit/77.", text);
        Assert.Contains("syncToRemote=false", text);
        Assert.Contains("add_ado_comment", text);
        Assert.DoesNotContain("Jira issue:", text);
    }

    [Theory]
    [InlineData(null, null, true)]
    [InlineData(false, null, false)]
    [InlineData(null, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, false)]
    public void RemoteSyncResolvesSyncToRemoteThenSyncToJiraThenTrue(bool? syncToRemote, bool? syncToJira, bool expected) =>
        Assert.Equal(expected, BoardTool.ResolveRemoteSync(syncToRemote, syncToJira));

    [Fact]
    public async Task AdoCommentUsesTheDefaultCardAndSavedTokenAndRecordsAllLinks()
    {
        var (card, _, client, comments) = await AdoFixtureAsync();
        _resolver.CurrentSessionId = "ado-worker";
        await _store.LinkSessionAsync(_project, card.Id, _resolver.CurrentSessionId, null, "base:codex", "codex", "Codex", BoardSessionRecord.LaunchOrigin, Ct);
        var links = new[] { "https://viberails.ai/shared/session?key=" + new string('a', 64), "https://viberails.ai/shared/session?key=" + new string('b', 64) };
        var tool = new BoardTool(_service, _resolver, _store, new BoardWorkflowService(_store, new BoardReviewService(_store, _service)), adoComments: comments);

        var result = await tool.AddAdoComment("Done and tested.", [.. links, links[0]], cancellationToken: Ct);

        Assert.StartsWith("Posted Azure DevOps comment 55 to work item #77: https://dev.azure.com/contoso/Web/_workitems/edit/77", result);
        client.Verify(c => c.AddCommentAsync("contoso", "Web", "secret", 77, "Done and tested.",
            It.Is<IReadOnlyList<string>>(l => l.SequenceEqual(links)), Ct), Times.Once);
        var receipt = Assert.Single((await _store.GetCardDetailAsync(_project, card.Id, Ct))!.Comments);
        Assert.Contains("Done and tested.", receipt.Body);
        Assert.All(links, link => Assert.Contains(link, receipt.Body));
        Assert.Equal("Codex", receipt.Author.Label);
        Assert.False(BoardJiraCommentPolicy.ShouldSync(receipt.Changes));
        // The receipt is not echoed back; only the linked session waits to be posted.
        Assert.DoesNotContain(await _store.GetPendingAdoDeliveriesAsync(Ct), d => d.Kind == "comment");
        Assert.DoesNotContain("add_ado_comment", BoardMcpAuthorization.ToolNames);
    }

    [Fact]
    public async Task AdoCommentFollowsTheOriginalWorkItemAfterMovingTheCardToAnotherBoard()
    {
        var (card, _, client, comments) = await AdoFixtureAsync();
        var board = await _store.CreateBoardAsync(_project, "Destination", Ct);
        var lane = (await _store.GetColumnsAsync(_project, Ct, board.Id))[0];
        await _store.MoveCardAsync(_project, card.Id, lane.Id, null, Ct);
        var result = await new BoardTool(_service, _resolver, _store, new BoardWorkflowService(_store, new BoardReviewService(_store, _service)), adoComments: comments)
            .AddAdoComment("Done", card: card.Key, cancellationToken: Ct);
        Assert.StartsWith("Posted Azure DevOps comment", result);
        Assert.Single(client.Invocations);
    }

    [Theory]
    [InlineData("token")]
    [InlineData("unlinked")]
    [InlineData("ambiguous")]
    [InlineData("other-project")]
    [InlineData("no-link")]
    public async Task AdoCommentRejectsAnUnusableConnectionWithoutPosting(string problem)
    {
        var (card, secrets, client, comments) = await AdoFixtureAsync();
        if (problem == "token") secrets.Setup(s => s.ReadToken(AdoConnectionId)).Returns((string?)null);
        if (problem == "unlinked") await _store.DeleteAdoConnectionAsync(_project, card.BoardId, AdoConnectionId, Ct);
        if (problem == "ambiguous") await _store.AddAdoLinkAsync(new(card.Id, AdoConnectionId, 78, 1, DateTime.UtcNow, null, DateTime.UtcNow), Ct);
        if (problem == "other-project")
        {
            await Assert.ThrowsAsync<AdoConfigException>(() => comments.PostAsync(Path.Combine(_root, "other"), card.Id, "Done", [], Ct));
            Assert.Empty(client.Invocations);
            return;
        }
        var target = card;
        if (problem == "no-link")
            target = await _store.CreateCardAsync(_project, new(null, "Local work", "", null, "medium", null, [], false), Ct);

        var result = await new BoardTool(_service, _resolver, _store, new BoardWorkflowService(_store, new BoardReviewService(_store, _service)), adoComments: comments)
            .AddAdoComment("Done", card: target.Key, cancellationToken: Ct);

        Assert.StartsWith("FAIL:", result);
        Assert.Empty(client.Invocations);
        Assert.Empty((await _store.GetCardDetailAsync(_project, target.Id, Ct))!.Comments);
    }

    [Fact]
    public async Task AdoCommentWritesNoReceiptForAnUnconfirmedPost()
    {
        var (card, _, client, comments) = await AdoFixtureAsync();
        client.Setup(c => c.AddCommentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(),
            It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AdoCommentResult(false, true, "Check the work item before retrying."));
        var result = await new BoardTool(_service, _resolver, _store, new BoardWorkflowService(_store, new BoardReviewService(_store, _service)), adoComments: comments)
            .AddAdoComment("Done", card: card.Key, cancellationToken: Ct);
        Assert.StartsWith("UNCONFIRMED:", result);
        Assert.Single(client.Invocations);
        Assert.Empty((await _store.GetCardDetailAsync(_project, card.Id, Ct))!.Comments);
    }

    [Fact]
    public async Task AdoCommentKeepsItsSuccessWhenTheLocalReceiptFails()
    {
        var (card, _, client, comments) = await AdoFixtureAsync();
        var service = new Mock<IBoardService>();
        service.Setup(s => s.AddCommentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<BoardAuthor>(), It.IsAny<string>(), Ct, false))
            .ThrowsAsync(new IOException("SECRET local failure"));
        var result = await new BoardTool(service.Object, _resolver, _store, new BoardWorkflowService(_store, new BoardReviewService(_store, service.Object)), adoComments: comments)
            .AddAdoComment("Done", card: card.Id, cancellationToken: Ct);
        Assert.StartsWith("Posted Azure DevOps comment", result);
        Assert.Contains("Do not repost", result);
        Assert.DoesNotContain("SECRET", result);
        Assert.Single(client.Invocations);
    }

    [Fact]
    public async Task AdoCommentRejectsOversizedInputAndLinksBeforePosting()
    {
        var (card, _, client, comments) = await AdoFixtureAsync();
        var tool = new BoardTool(_service, _resolver, _store, new BoardWorkflowService(_store, new BoardReviewService(_store, _service)), adoComments: comments);
        Assert.StartsWith("FAIL:", await tool.AddAdoComment(new string('x', 20001), card: card.Key, cancellationToken: Ct));
        Assert.StartsWith("FAIL:", await tool.AddAdoComment("Done", Enumerable.Repeat("invalid", 21).ToArray(), card.Key, Ct));
        Assert.StartsWith("FAIL:", await tool.AddAdoComment("Done", ["https://evil.example/"], card.Key, Ct));
        Assert.Empty(client.Invocations);
    }

    [Fact]
    public async Task AdoCommentWithoutTheServiceFailsAfterResolvingTheCard()
    {
        var (card, _, _, _) = await AdoFixtureAsync();
        var tool = new BoardTool(_service, _resolver, _store, new BoardWorkflowService(_store, new BoardReviewService(_store, _service)));
        Assert.Equal("FAIL: Azure DevOps commenting is unavailable in this host.", await tool.AddAdoComment("Done", card: card.Key, cancellationToken: Ct));
    }

    [Fact]
    public async Task ListBoardsNamesEachDedicatedTrackerBoardsProvider()
    {
        await _store.EnsureDefaultColumnsAsync(_project, Ct);
        var local = (await _store.GetBoardsAsync(_project, Ct))[0];
        var ado = await _store.CreateBoardAsync(_project, "Azure DevOps · Web Team", Ct);
        var jira = await _store.CreateBoardAsync(_project, "Jira · App", Ct);
        await _store.SaveAdoConnectionAsync(AdoConnection(ado.Id) with { DedicatedBoard = true }, Ct);
        await _store.SaveJiraConnectionAsync(new BoardJiraConnectionRecord("jira-connection", _project, jira.Id, "https://jira.example", "user@example.com",
            true, "saved", null, "project = APP", true, null, null, null, null, null, DedicatedBoard: true), Ct);
        var tool = new BoardTool(_service, _resolver, _store, new BoardWorkflowService(_store, new BoardReviewService(_store, _service)));

        var lines = (await tool.ListBoards(Ct)).Split('\n');

        Assert.Contains("provider: Azure DevOps", Assert.Single(lines, l => l.Contains($"id {ado.Id}")));
        Assert.Contains("provider: Jira", Assert.Single(lines, l => l.Contains($"id {jira.Id}")));
        Assert.DoesNotContain("provider:", Assert.Single(lines, l => l.Contains($"id {local.Id}")));
    }

    private BoardAdoConnectionRecord AdoConnection(string boardId) => new(
        AdoConnectionId, _project, boardId, "contoso", "Web", "Web Team", "Stories", null,
        "https://dev.azure.com/contoso/Web/_boards/board/t/Web%20Team/Stories", "Stories", null, null, null, null,
        true, BoardAdoAuthStatus.Saved, true, null, null, false, null, null, null);

    private async Task<(BoardCardRecord Card, Mock<IAdoSecretStore> Secrets, Mock<IAdoCommentClient> Client, AdoCommentService Comments)> AdoFixtureAsync()
    {
        await _store.EnsureDefaultColumnsAsync(_project, Ct);
        var card = await _store.CreateCardAsync(_project, new(null, "Azure DevOps work", "", null, "medium", null, [], false), Ct);
        await _store.SaveAdoConnectionAsync(AdoConnection(card.BoardId), Ct);
        await _store.AddAdoLinkAsync(new(card.Id, AdoConnectionId, 77, 4, DateTime.UtcNow, null, DateTime.UtcNow), Ct);
        var secrets = new Mock<IAdoSecretStore>(MockBehavior.Strict);
        secrets.Setup(s => s.ReadToken(AdoConnectionId)).Returns("secret");
        var client = new Mock<IAdoCommentClient>(MockBehavior.Strict);
        client.Setup(c => c.AddCommentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(),
            It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AdoCommentResult(true, false, "Posted", "55"));
        return (card, secrets, client, new AdoCommentService(_store, secrets.Object, client.Object, new AdoPullLock(Path.Combine(_root, ".ado-comment.lock"))));
    }
}
