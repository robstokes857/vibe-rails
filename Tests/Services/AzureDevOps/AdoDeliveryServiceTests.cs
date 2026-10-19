using Microsoft.Data.Sqlite;
using Moq;
using VibeRails.Services.AzureDevOps;
using VibeRails.Services.Board;
using VibeRails.Services.Integrations.VibeCodeRemote;
using Xunit;

namespace Tests.Services.AzureDevOps;

/// <summary>
/// Board activity to the linked work item (VIBE-168), mirroring the Jira drain: the trigger and
/// session queue from board-ado-delivery/1, re-validation before sending, a saved share URL before
/// the POST, and no automatic retry of an uncertain send.
/// </summary>
public sealed class AdoDeliveryServiceTests : IDisposable
{
    private const string ConnectionId = "ado_aaaaaaaaaaaa";
    private const string ShareUrl = "https://viberails.ai/shared/session?key=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private readonly string root = Path.Combine(Path.GetTempPath(), "ado-delivery-" + Guid.NewGuid().ToString("N"));
    private readonly BoardStore store;
    private readonly BoardService boards;
    private readonly Mock<IAdoSecretStore> secrets = new();
    private readonly Mock<IAdoCommentClient> client = new();
    private readonly Mock<IAdoSessionSharing> sharing = new();
    private CancellationToken Ct => TestContext.Current.CancellationToken;
    private string Project => Path.Combine(root, "project");

    public AdoDeliveryServiceTests()
    {
        Directory.CreateDirectory(root);
        var cs = $"Data Source={Path.Combine(root, "board.db")};Pooling=False";
        store = new(cs, cs);
        boards = new(store, Mock.Of<IBoardCommitService>(), new NullBoardLiveSessionProbe());
        secrets.Setup(s => s.ReadToken(ConnectionId)).Returns("test-only-pat");
        client.Setup(c => c.AddCommentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(),
            It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AdoCommentResult(true, false, "Posted", "7"));
        sharing.Setup(s => s.CreateAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SessionShareResponse(true, "pending_upload", "Available after upload", ShareUrl));
    }

    private AdoDeliveryService Service() => new(store, secrets.Object, client.Object, sharing.Object, new(Path.Combine(root, "ado.lock")));

    private async Task<BoardCardRecord> CardAsync()
    {
        await store.EnsureDefaultColumnsAsync(Project, Ct);
        var card = await store.CreateCardAsync(Project, new(null, "Test", "", null, "medium", null, [], false), Ct);
        await store.AddCommentAsync(Project, card.Id, BoardAuthor.User(), "Before linking", Ct);
        await store.SaveAdoConnectionAsync(new(ConnectionId, Project, card.BoardId, "contoso", "Web App", "Web Team", "Stories", null,
            "https://dev.azure.com/contoso/Web%20App/_boards/board/t/Web%20Team/Stories", "Stories", null, null, null, null,
            true, BoardAdoAuthStatus.Saved, true, null, null, false, null, null, null), Ct);
        await store.AddAdoLinkAsync(new(card.Id, ConnectionId, 42, 3, DateTime.UtcNow, null, DateTime.UtcNow), Ct);
        return card;
    }

    [Fact]
    public async Task CommentsQueueAtomically_InternalNotesAndHistoricalDiscussionStayOffAzureDevOps()
    {
        var card = await CardAsync();
        var comment = await boards.AddCommentAsync(Project, card.Id, BoardAuthor.User(), "Ready for review", Ct);
        var note = await boards.AddNoteAsync(Project, card.Id, BoardAuthor.Agent("Codex", "codex", null), "Internal scratchpad", Ct, false);
        Assert.False(note!.SyncToJira);
        Assert.True(comment!.SyncToJira);
        Assert.Equal(comment.Id, Assert.Single(await store.GetPendingAdoDeliveriesAsync(Ct)).SourceId);
        Assert.Empty(await store.GetPendingJiraDeliveriesAsync(Ct));

        await Service().DrainAsync(Ct);
        await Service().DrainAsync(Ct);

        client.Verify(c => c.AddCommentAsync("contoso", "Web App", "test-only-pat", 42, "You: Ready for review",
            It.Is<IReadOnlyList<string>>(l => l.Count == 0), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Single(client.Invocations);
        Assert.Empty(sharing.Invocations);
        Assert.Equal("sent", Assert.Single(await store.GetAdoDeliveriesAsync(Project, card.Id, Ct)).Status);
        Assert.Empty(await store.GetAdoDeliveriesAsync(Project + "-foreign", card.Id, Ct));

        var detail = (await boards.GetCardAsync(Project, card.Id, Ct))!;
        Assert.Equal("ado", detail.RemoteProvider);
        Assert.Equal("#42", detail.RemoteIssueKey);
        Assert.Equal("https://dev.azure.com/contoso/Web%20App/_workitems/edit/42", detail.RemoteIssueUrl);
        Assert.Equal("sent", Assert.Single(detail.RemoteDeliveries).Status);
        Assert.Null(detail.JiraIssueKey);
        Assert.Empty(detail.JiraDeliveries);
    }

    [Fact]
    public async Task SessionAttachmentCreatesOneShareAndPostsItToTheOriginalWorkItemAfterAMove()
    {
        var card = await CardAsync();
        var session = Guid.NewGuid().ToString();
        await boards.LinkSessionAsync(Project, card.Id, session, null, "base:codex", "codex", "Working session", "launch", Ct);
        await boards.AttachSessionAsync(Project, card.Id, session, null, Ct);
        var other = await store.CreateBoardAsync(Project, "Other", Ct);
        await store.MoveCardAsync(Project, card.Id, (await store.GetColumnsAsync(Project, Ct, other.Id))[0].Id, null, Ct);

        await Service().DrainAsync(Ct);
        await Service().DrainAsync(Ct);

        sharing.Verify(s => s.CreateAsync(Guid.Parse(session), "#42 · VibeRails session linked: Working session", It.IsAny<CancellationToken>()), Times.Once);
        client.Verify(c => c.AddCommentAsync("contoso", "Web App", "test-only-pat", 42,
            "VibeRails session linked: Working session", It.Is<IReadOnlyList<string>>(l => l.Count == 1 && l[0] == ShareUrl),
            It.IsAny<CancellationToken>()), Times.Once);
        var delivery = Assert.Single(await store.GetAdoDeliveriesAsync(Project, card.Id, Ct));
        Assert.Equal("sent", delivery.Status);
        Assert.Equal(ShareUrl, delivery.Url);
    }

    [Theory]
    [InlineData("delete-comment")]
    [InlineData("unlink")]
    [InlineData("other-connection")]
    public async Task RemovedActivityOrConnectionIsNeverPosted(string action)
    {
        var card = await CardAsync();
        var comment = await boards.AddCommentAsync(Project, card.Id, BoardAuthor.User(), "Do not send", Ct);
        if (action == "unlink") await store.DeleteAdoConnectionAsync(Project, card.BoardId, ConnectionId, Ct);
        else if (action == "other-connection")
        {
            // A new organization mints a new connection id; the queued event still names the old one.
            var current = (await store.GetAdoConnectionAsync(Project, card.BoardId, Ct))!;
            await store.SaveAdoConnectionAsync(current with { Id = "ado_bbbbbbbbbbbb", Organization = "fabrikam" }, Ct);
        }
        else await boards.DeleteCommentAsync(Project, card.Id, comment!.Id, BoardAuthor.User(), Ct);

        await Service().DrainAsync(Ct);

        Assert.Empty(client.Invocations);
        Assert.Equal("cancelled", Assert.Single(await store.GetAdoDeliveriesAsync(Project, card.Id, Ct)).Status);
    }

    [Fact]
    public async Task AMissingTokenFailsWithoutPosting()
    {
        var card = await CardAsync();
        await boards.AddCommentAsync(Project, card.Id, BoardAuthor.User(), "Update", Ct);
        secrets.Setup(s => s.ReadToken(ConnectionId)).Returns((string?)null);

        await Service().DrainAsync(Ct);

        Assert.Empty(client.Invocations);
        var failed = Assert.Single(await store.GetAdoDeliveriesAsync(Project, card.Id, Ct));
        Assert.Equal("failed", failed.Status);
        Assert.Contains("Reconnect Azure DevOps", failed.Message);
    }

    [Theory]
    [InlineData(true, "uncertain")]
    [InlineData(false, "failed")]
    public async Task UnconfirmedAndRejectedWritesAreVisibleAndNeverBlindlyRetried(bool mayHavePosted, string status)
    {
        var card = await CardAsync();
        await boards.AddCommentAsync(Project, card.Id, BoardAuthor.User(), "Update", Ct);
        client.Setup(c => c.AddCommentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(),
            It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AdoCommentResult(false, mayHavePosted, "Check Azure DevOps"));

        await Service().DrainAsync(Ct);
        await Service().DrainAsync(Ct);

        Assert.Single(client.Invocations);
        Assert.Equal(status, Assert.Single((await boards.GetCardAsync(Project, card.Id, Ct))!.RemoteDeliveries).Status);
    }

    [Fact]
    public async Task AThrowingClientIsUncertainAndNeverRetried()
    {
        var card = await CardAsync();
        await boards.AddCommentAsync(Project, card.Id, BoardAuthor.User(), "Update", Ct);
        client.Setup(c => c.AddCommentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(),
            It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("SECRET local failure"));

        await Service().DrainAsync(Ct);
        await Service().DrainAsync(Ct);

        Assert.Single(client.Invocations);
        var delivery = Assert.Single(await store.GetAdoDeliveriesAsync(Project, card.Id, Ct));
        Assert.Equal("uncertain", delivery.Status);
        Assert.DoesNotContain("SECRET", delivery.Message);
    }

    [Fact]
    public async Task CrashAfterClaimDoesNotRecreateShareOrRepost()
    {
        var card = await CardAsync();
        await boards.AddCommentAsync(Project, card.Id, BoardAuthor.User(), "Update", Ct);
        var pending = Assert.Single(await store.GetPendingAdoDeliveriesAsync(Ct));
        Assert.True(await store.SetAdoDeliveryAsync(pending.Id, "pending", "sending", null, null, Ct));
        Assert.False(await store.SetAdoDeliveryAsync(pending.Id, "pending", "sending", null, null, Ct));

        await Service().DrainAsync(Ct);

        Assert.Empty(client.Invocations);
        Assert.Equal("uncertain", Assert.Single(await store.GetAdoDeliveriesAsync(Project, card.Id, Ct)).Status);
    }

    [Fact]
    public async Task ShareFailureNeverPostsAnEmptyOrInventedLink()
    {
        var card = await CardAsync();
        await boards.LinkSessionAsync(Project, card.Id, Guid.NewGuid().ToString(), null, "base:codex", "codex", "Session", "launch", Ct);
        sharing.Setup(s => s.CreateAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SessionShareResponse(false, "no_api_key", "Sign in before sharing."));

        await Service().DrainAsync(Ct);

        Assert.Empty(client.Invocations);
        var delivery = Assert.Single(await store.GetAdoDeliveriesAsync(Project, card.Id, Ct));
        Assert.Equal("failed", delivery.Status);
        Assert.Equal("Sign in before sharing.", delivery.Message);
    }

    /// <summary>A local "busy" share response sent nothing, so the event waits for the next drain.</summary>
    [Fact]
    public async Task ALocalBusyShareIsRetriedOnTheNextDrain()
    {
        var card = await CardAsync();
        await boards.LinkSessionAsync(Project, card.Id, Guid.NewGuid().ToString(), null, "base:codex", "codex", "Session", "launch", Ct);
        sharing.SetupSequence(s => s.CreateAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SessionShareResponse(false, "busy", "Another sharing link is being created. Try again shortly."))
            .ReturnsAsync(new SessionShareResponse(true, "pending_upload", "Available after upload", ShareUrl));

        await Service().DrainAsync(Ct);
        Assert.Empty(client.Invocations);
        Assert.Equal("pending", Assert.Single(await store.GetAdoDeliveriesAsync(Project, card.Id, Ct)).Status);

        await Service().DrainAsync(Ct);
        Assert.Equal(2, sharing.Invocations.Count);
        Assert.Single(client.Invocations);
        var sent = Assert.Single(await store.GetAdoDeliveriesAsync(Project, card.Id, Ct));
        Assert.Equal("sent", sent.Status);
        Assert.Equal(ShareUrl, sent.Url);
    }

    /// <summary>The pull's own notes and an explicit add_ado_comment receipt are never echoed back.</summary>
    [Fact]
    public async Task PullNotesAndExplicitPostReceiptsAreNotQueued()
    {
        var card = await CardAsync();
        await boards.AddCommentAsync(Project, card.Id, BoardAuthor.Agent("Azure DevOps", null, null), "#42 is now \"Active\".", Ct);
        await boards.AddCommentAsync(Project, card.Id, BoardAuthor.Agent("Codex", "codex", null), "Posted Azure DevOps comment 7", Ct, syncToJira: false);
        Assert.Empty(await store.GetPendingAdoDeliveriesAsync(Ct));
    }

    [Fact]
    public async Task AHeldLockSkipsTheDrainWithoutTouchingAnything()
    {
        var card = await CardAsync();
        await boards.AddCommentAsync(Project, card.Id, BoardAuthor.User(), "Update", Ct);
        var pullLock = new AdoPullLock(Path.Combine(root, "ado.lock"));
        using (pullLock.TryAcquire())
            await Service().DrainAsync(Ct);
        Assert.Empty(client.Invocations);
        Assert.Equal("pending", Assert.Single(await store.GetAdoDeliveriesAsync(Project, card.Id, Ct)).Status);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(root, true);
    }
}
