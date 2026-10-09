using Microsoft.Data.Sqlite;
using Moq;
using VibeRails.Services.Board;
using VibeRails.Services.Integrations.VibeCodeRemote;
using VibeRails.Services.Jira;
using Xunit;

namespace Tests.Services.Jira;

public sealed class JiraDeliveryServiceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "jira-delivery-" + Guid.NewGuid().ToString("N"));
    private readonly BoardStore store;
    private readonly BoardService boards;
    private readonly Mock<IJiraSecretStore> secrets = new();
    private readonly Mock<IJiraCommentClient> client = new();
    private readonly Mock<IJiraSessionSharing> sharing = new();
    private CancellationToken Ct => TestContext.Current.CancellationToken;
    private string Project => Path.Combine(root, "project");
    private const string ShareUrl = "https://viberails.ai/shared/session?key=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    public JiraDeliveryServiceTests()
    {
        Directory.CreateDirectory(root);
        var cs = $"Data Source={Path.Combine(root, "board.db")};Pooling=False";
        store = new(cs, cs);
        boards = new(store, Mock.Of<IBoardCommitService>(), new NullBoardLiveSessionProbe());
        secrets.Setup(s => s.ReadToken("jira")).Returns("test-only-token");
        client.Setup(c => c.AddCommentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new JiraCommentResult(true, false, "Posted", "123"));
        sharing.Setup(s => s.CreateAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SessionShareResponse(true, "pending_upload", "Available after upload", ShareUrl));
    }

    private JiraDeliveryService Service() => new(store, secrets.Object, client.Object, sharing.Object, new(Path.Combine(root, "jira.lock")));

    private async Task<BoardCardRecord> CardAsync()
    {
        await store.EnsureDefaultColumnsAsync(Project, Ct);
        var card = await store.CreateCardAsync(Project, new(null, "Test", "", null, "medium", null, [], false), Ct);
        await store.AddCommentAsync(Project, card.Id, BoardAuthor.User(), "Before linking", Ct);
        await store.SaveJiraConnectionAsync(new("jira", Project, card.BoardId, "https://jira.example", "test@example.com",
            true, "saved", null, "project = TEST", true, null, null, null, null, null), Ct);
        await store.AddJiraLinkAsync(new(card.Id, "jira", "100", "TEST-1", null, DateTime.UtcNow, DateTime.UtcNow), Ct);
        return card;
    }

    [Fact]
    public async Task CommentsQueueAtomically_InternalNotesAndHistoricalDiscussionStayOffJira()
    {
        var card = await CardAsync();
        var comment = await boards.AddCommentAsync(Project, card.Id, BoardAuthor.User(), "Ready for review", Ct);
        var note = await boards.AddNoteAsync(Project, card.Id, BoardAuthor.Agent("Codex", "codex", null), "Internal scratchpad", Ct, false);
        Assert.False(note!.SyncToJira);
        Assert.True(comment!.SyncToJira);
        Assert.Equal(comment.Id, Assert.Single(await store.GetPendingJiraDeliveriesAsync(Ct)).SourceId);
        await Service().DrainAsync(Ct);
        await Service().DrainAsync(Ct);
        Assert.Single(client.Invocations);
        Assert.Empty(sharing.Invocations);
        Assert.Equal("sent", Assert.Single(await store.GetJiraDeliveriesAsync(Project, card.Id, Ct)).Status);
        Assert.Empty(await store.GetJiraDeliveriesAsync(Project + "-foreign", card.Id, Ct));
        Assert.Equal("https://jira.example/browse/TEST-1", (await boards.GetCardAsync(Project, card.Id, Ct))!.JiraIssueUrl);
    }

    [Fact]
    public async Task SessionAttachmentCreatesOneShareAndPostsItToTheOriginalIssueAfterAMove()
    {
        var card = await CardAsync();
        var session = Guid.NewGuid().ToString();
        await boards.LinkSessionAsync(Project, card.Id, session, null, "base:codex", "codex", "Working session", "launch", Ct);
        await boards.AttachSessionAsync(Project, card.Id, session, null, Ct);
        var other = await store.CreateBoardAsync(Project, "Other", Ct);
        await store.MoveCardAsync(Project, card.Id, (await store.GetColumnsAsync(Project, Ct, other.Id))[0].Id, null, Ct);
        await Service().DrainAsync(Ct);
        await Service().DrainAsync(Ct);
        Assert.Single(sharing.Invocations);
        client.Verify(c => c.AddCommentAsync("https://jira.example", "test@example.com", "test-only-token", "100",
            "VibeRails session linked: Working session", It.Is<IReadOnlyList<string>>(l => l.Count == 1 && l[0] == ShareUrl), Ct), Times.Once);
        var delivery = Assert.Single(await store.GetJiraDeliveriesAsync(Project, card.Id, Ct));
        Assert.Equal("sent", delivery.Status);
        Assert.Equal(ShareUrl, delivery.Url);
    }

    [Theory]
    [InlineData("delete-comment")]
    [InlineData("unlink")]
    public async Task RemovedActivityOrConnectionIsNeverPosted(string action)
    {
        var card = await CardAsync();
        var comment = await boards.AddCommentAsync(Project, card.Id, BoardAuthor.User(), "Do not send", Ct);
        if (action == "unlink") await store.DeleteJiraConnectionAsync(Project, card.BoardId, "jira", Ct);
        else await boards.DeleteCommentAsync(Project, card.Id, comment!.Id, BoardAuthor.User(), Ct);
        await Service().DrainAsync(Ct);
        Assert.Empty(client.Invocations);
        Assert.Equal("cancelled", Assert.Single(await store.GetJiraDeliveriesAsync(Project, card.Id, Ct)).Status);
    }

    [Theory]
    [InlineData(true, "uncertain")]
    [InlineData(false, "failed")]
    public async Task UnconfirmedAndRejectedWritesAreVisibleAndNeverBlindlyRetried(bool mayHavePosted, string status)
    {
        var card = await CardAsync();
        await boards.AddCommentAsync(Project, card.Id, BoardAuthor.User(), "Update", Ct);
        client.Setup(c => c.AddCommentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new JiraCommentResult(false, mayHavePosted, "Check Jira"));
        await Service().DrainAsync(Ct);
        await Service().DrainAsync(Ct);
        Assert.Single(client.Invocations);
        Assert.Equal(status, Assert.Single((await boards.GetCardAsync(Project, card.Id, Ct))!.JiraDeliveries).Status);
    }

    [Fact]
    public async Task CrashAfterClaimDoesNotRecreateShareOrRepost()
    {
        var card = await CardAsync();
        await boards.AddCommentAsync(Project, card.Id, BoardAuthor.User(), "Update", Ct);
        var pending = Assert.Single(await store.GetPendingJiraDeliveriesAsync(Ct));
        Assert.True(await store.SetJiraDeliveryAsync(pending.Id, "pending", "sending", null, null, Ct));
        Assert.False(await store.SetJiraDeliveryAsync(pending.Id, "pending", "sending", null, null, Ct));
        await Service().DrainAsync(Ct);
        Assert.Empty(client.Invocations);
        Assert.Equal("uncertain", Assert.Single(await store.GetJiraDeliveriesAsync(Project, card.Id, Ct)).Status);
    }

    [Fact]
    public async Task ShareFailureNeverPostsAnEmptyOrInventedLink()
    {
        var card = await CardAsync();
        await boards.LinkSessionAsync(Project, card.Id, Guid.NewGuid().ToString(), null, "base:codex", "codex", "Session", "launch", Ct);
        sharing.Setup(s => s.CreateAsync(It.IsAny<Guid>(), It.IsAny<string>(), Ct))
            .ReturnsAsync(new SessionShareResponse(false, "no_api_key", "Sign in before sharing."));
        await Service().DrainAsync(Ct);
        Assert.Empty(client.Invocations);
        Assert.Equal("Sign in before sharing.", Assert.Single(await store.GetJiraDeliveriesAsync(Project, card.Id, Ct)).Message);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(root, true);
    }
}
