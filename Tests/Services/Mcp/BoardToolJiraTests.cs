using Moq;
using VibeRails.Services.Board;
using VibeRails.Services.Jira;
using VibeRails.Services.Mcp.Tools;
using VibeRails.Services.Terminal;
using Xunit;

namespace Tests.Services.Mcp;

public sealed partial class BoardToolTests
{
    [Fact]
    public async Task OrdinaryCommentsSyncByDefaultAndMcpCanKeepInternalNotes()
    {
        var (card, _, _, _) = await JiraFixtureAsync();
        var tool = new BoardTool(_service, _resolver, _store, new BoardWorkflowService(_store, new BoardReviewService(_store, _service)));
        await tool.AddBoardComment("Shared update", card.Key, Ct);
        await tool.AddBoardComment("Internal note", card.Key, Ct, syncToJira: false);
        await tool.AppendBoardNote("Another internal note", card.Key, Ct, syncToJira: false);
        Assert.Single(await _store.GetPendingJiraDeliveriesAsync(Ct));
        var detail = (await _service.GetCardAsync(_project, card.Id, Ct))!;
        Assert.Equal(2, detail.Comments.Count(c => !c.SyncToJira));
        Assert.Contains("syncToJira=false", await tool.GetBoardCard(card.Key, cancellationToken: Ct));
    }

    [Fact]
    public async Task JiraCommentUsesDefaultCardSavedCredentialsAndRecordsAllLinks()
    {
        var (card, _, client, comments) = await JiraFixtureAsync();
        _resolver.CurrentSessionId = "jira-worker";
        await _store.LinkSessionAsync(_project, card.Id, _resolver.CurrentSessionId, null, "base:codex", "codex", "Codex", BoardSessionRecord.LaunchOrigin, Ct);
        var links = new[] { "https://viberails.ai/shared/session?key=" + new string('a', 64), "https://viberails.ai/shared/session?key=" + new string('b', 64) };
        var tool = new BoardTool(_service, _resolver, _store, new BoardWorkflowService(_store, new BoardReviewService(_store, _service)), jiraComments: comments);
        var result = await tool.AddJiraComment("Done and tested.", [.. links, links[0]], cancellationToken: Ct);
        Assert.StartsWith("Posted Jira comment 55 to APP-7:", result);
        Assert.Contains("https://jira.example/secure/ViewIssue.jspa?id=10007&focusedCommentId=55", result);
        client.Verify(c => c.AddCommentAsync("https://jira.example", "user@example.com", "secret", "10007", "Done and tested.",
            It.Is<IReadOnlyList<string>>(l => l.SequenceEqual(links)), Ct), Times.Once);
        var receipt = Assert.Single((await _store.GetCardDetailAsync(_project, card.Id, Ct))!.Comments);
        Assert.Contains("Done and tested.", receipt.Body);
        Assert.All(links, link => Assert.Contains(link, receipt.Body));
        Assert.Equal("Codex", receipt.Author.Label);
        Assert.DoesNotContain("add_jira_comment", BoardMcpAuthorization.ToolNames);
        Assert.Contains("add_jira_comment", await tool.GetBoardCard(card.Key, cancellationToken: Ct));
    }

    [Fact]
    public async Task JiraCommentFollowsOriginalIssueAfterMovingCardToAnotherBoard()
    {
        var (card, _, client, comments) = await JiraFixtureAsync();
        var board = await _store.CreateBoardAsync(_project, "Destination", Ct);
        var lane = (await _store.GetColumnsAsync(_project, Ct, board.Id))[0];
        await _store.MoveCardAsync(_project, card.Id, lane.Id, null, Ct);
        var result = await new BoardTool(_service, _resolver, _store, new BoardWorkflowService(_store, new BoardReviewService(_store, _service)), jiraComments: comments).AddJiraComment("Done", card: card.Key, cancellationToken: Ct);
        Assert.StartsWith("Posted Jira comment", result);
        Assert.Single(client.Invocations);
    }

    [Fact]
    public async Task JiraLookupRejectsWrongProjectAndDeletedCardsButRetainsHistoricalLink()
    {
        var (card, _, client, comments) = await JiraFixtureAsync();
        Assert.Empty(await _store.GetJiraLinksForCardAsync(Path.Combine(_root, "other"), card.Id, Ct));
        await Assert.ThrowsAsync<JiraConfigException>(() => comments.PostAsync(Path.Combine(_root, "other"), card.Id, "Done", [], Ct));
        await _store.DeleteCardAsync(_project, card.Id, Ct);
        Assert.Empty(await _store.GetJiraLinksForCardAsync(_project, card.Id, Ct));
        Assert.NotNull(await _store.FindJiraLinkAsync("jira-connection", "10007", Ct));
        Assert.Empty(client.Invocations);
    }

    [Theory]
    [InlineData("token")]
    [InlineData("unlinked")]
    [InlineData("ambiguous")]
    public async Task JiraCommentRejectsUnusableConnectionWithoutPosting(string problem)
    {
        var (card, secrets, client, comments) = await JiraFixtureAsync();
        if (problem == "token") secrets.Setup(s => s.ReadToken("jira-connection")).Returns((string?)null);
        if (problem == "unlinked") await _store.DeleteJiraConnectionAsync(_project, card.BoardId, "jira-connection", Ct);
        if (problem == "ambiguous") await _store.AddJiraLinkAsync(new(card.Id, "jira-connection", "10008", "APP-8", null, DateTime.UtcNow, DateTime.UtcNow), Ct);
        Assert.StartsWith("FAIL:", await new BoardTool(_service, _resolver, _store, new BoardWorkflowService(_store, new BoardReviewService(_store, _service)), jiraComments: comments).AddJiraComment("Done", card: card.Key, cancellationToken: Ct));
        Assert.Empty(client.Invocations);
        Assert.Empty((await _store.GetCardDetailAsync(_project, card.Id, Ct))!.Comments);
    }

    [Fact]
    public async Task JiraCommentDoesNotWriteReceiptForUnconfirmedPost()
    {
        var (card, _, client, comments) = await JiraFixtureAsync();
        client.Setup(c => c.AddCommentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new JiraCommentResult(false, true, "Check Jira before retrying."));
        var result = await new BoardTool(_service, _resolver, _store, new BoardWorkflowService(_store, new BoardReviewService(_store, _service)), jiraComments: comments).AddJiraComment("Done", card: card.Key, cancellationToken: Ct);
        Assert.StartsWith("UNCONFIRMED:", result);
        Assert.Single(client.Invocations);
        Assert.Empty((await _store.GetCardDetailAsync(_project, card.Id, Ct))!.Comments);
    }

    [Fact]
    public async Task JiraCommentPreservesSuccessWhenLocalReceiptFails()
    {
        var (card, _, client, comments) = await JiraFixtureAsync();
        var service = new Mock<IBoardService>();
        service.Setup(s => s.AddCommentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<BoardAuthor>(), It.IsAny<string>(), Ct))
            .ThrowsAsync(new IOException("SECRET local failure"));
        var result = await new BoardTool(service.Object, _resolver, _store, new BoardWorkflowService(_store, new BoardReviewService(_store, service.Object)), jiraComments: comments).AddJiraComment("Done", card: card.Id, cancellationToken: Ct);
        Assert.StartsWith("Posted Jira comment", result);
        Assert.Contains("Do not repost", result);
        Assert.DoesNotContain("SECRET", result);
        Assert.Single(client.Invocations);
    }

    [Fact]
    public async Task JiraCommentRejectsOversizedInputAndLinksBeforePosting()
    {
        var (card, _, client, comments) = await JiraFixtureAsync();
        var tool = new BoardTool(_service, _resolver, _store, new BoardWorkflowService(_store, new BoardReviewService(_store, _service)), jiraComments: comments);
        Assert.StartsWith("FAIL:", await tool.AddJiraComment(new string('x', 20001), card: card.Key, cancellationToken: Ct));
        Assert.StartsWith("FAIL:", await tool.AddJiraComment("Done", Enumerable.Repeat("invalid", 21).ToArray(), card.Key, Ct));
        Assert.StartsWith("FAIL:", await tool.AddJiraComment("Done", ["https://evil.example/"], card.Key, Ct));
        Assert.Empty(client.Invocations);
    }

    private async Task<(BoardCardRecord Card, Mock<IJiraSecretStore> Secrets, Mock<IJiraCommentClient> Client, JiraCommentService Comments)> JiraFixtureAsync()
    {
        await _store.EnsureDefaultColumnsAsync(_project, Ct);
        var card = await _store.CreateCardAsync(_project, new(null, "Jira work", "", null, "medium", null, [], false), Ct);
        await _store.SaveJiraConnectionAsync(new("jira-connection", _project, card.BoardId, "https://jira.example", "user@example.com",
            true, "saved", null, "project = APP", true, null, null, null, null, null), Ct);
        await _store.AddJiraLinkAsync(new(card.Id, "jira-connection", "10007", "APP-7", null, DateTime.UtcNow, DateTime.UtcNow), Ct);
        var secrets = new Mock<IJiraSecretStore>(MockBehavior.Strict);
        secrets.Setup(s => s.ReadToken("jira-connection")).Returns("secret");
        var client = new Mock<IJiraCommentClient>(MockBehavior.Strict);
        client.Setup(c => c.AddCommentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new JiraCommentResult(true, false, "Posted", "55"));
        return (card, secrets, client, new JiraCommentService(_store, secrets.Object, client.Object, new JiraPullLock(Path.Combine(_root, ".jira-comment.lock"))));
    }
}
