using Moq;
using VibeRails.Data.Sqlite;
using VibeRails.Services.Board;
using VibeRails.Services.Board.Sync;
using Xunit;

namespace Tests.Services.Board;

/// <summary>
/// An invitation id becomes a path segment twice: in the dashboard's request to this backend and in
/// this backend's request to viberails.ai. An id such as ".." is resolved away by either client and
/// would address the board itself, so only opaque ids are returned or accepted (VIBE-310).
/// </summary>
public sealed class BoardSharingServiceTests : IDisposable
{
    private const string Destination = "owner-account";
    private readonly string root = Path.Combine(Path.GetTempPath(), "board-sharing-service-" + Guid.NewGuid().ToString("N"));
    private readonly string remoteBoard = Guid.NewGuid().ToString("D");
    private readonly BoardStore store;
    private readonly Mock<IBoardSyncClient> client = new();
    private readonly Mock<IBoardSyncService> sync = new(MockBehavior.Strict);
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public BoardSharingServiceTests()
    {
        Directory.CreateDirectory(root);
        var cs = $"Data Source={Path.Combine(root, "board.db")};Pooling=False";
        store = new(cs, cs);
        client.SetupGet(c => c.IsConfigured).Returns(true);
        client.SetupGet(c => c.DestinationKey).Returns(Destination);
        client.Setup(c => c.DescribeAsync(remoteBoard, It.IsAny<CancellationToken>(), Destination))
            .ReturnsAsync(new BoardRemoteDescriptor(remoteBoard, "Local", "VB", "VB", [new("col_a", "Todo", null, 0)], IsOwner: true, 0));
        client.Setup(c => c.RemoveInviteAsync(remoteBoard, It.IsAny<string>(), It.IsAny<CancellationToken>(), Destination))
            .ReturnsAsync(new BoardSharingResult());
        client.Setup(c => c.SaveInviteAsync(remoteBoard, It.IsAny<string?>(), It.IsAny<BoardSharingEmailRequest>(), It.IsAny<CancellationToken>(), Destination))
            .ReturnsAsync(new BoardSharingResult());
    }

    public void Dispose()
    {
        try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private async Task<(BoardSharingService Service, string BoardId)> LinkedBoardAsync()
    {
        var board = await store.CreateBoardAsync(root, "Local", Ct);
        var now = DateTime.UtcNow;
        await store.SaveSyncLinkAsync(new BoardSyncLinkRecord(board.Id, remoteBoard, 0, true, null, null, null, now, now, root, "Local", Destination), Ct);
        return (new BoardSharingService(store, client.Object, sync.Object), board.Id);
    }

    private void HostedInvitations(params string[] ids) =>
        client.Setup(c => c.GetSharingAsync(remoteBoard, It.IsAny<CancellationToken>(), Destination))
            .ReturnsAsync(new BoardSharingOverview(5, ids.Select(id => new BoardSharingInvite(id, "person@example.com", "pending")).ToList()));

    [Fact]
    public async Task HostedInvitationsWithOpaqueIdsAreListed()
    {
        var (service, boardId) = await LinkedBoardAsync();
        var id = Guid.NewGuid().ToString("D");
        HostedInvitations(id, "invite_2");

        var state = await service.GetAsync(root, boardId, Ct);

        Assert.True(state.IsOwner);
        Assert.Equal([id, "invite_2"], state.Sharing!.Invitations.Select(invite => invite.Id));
    }

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("../other")]
    [InlineData("a/b")]
    [InlineData(@"a\b")]
    [InlineData("a?x=1")]
    [InlineData("a#b")]
    [InlineData("%2e%2e")]
    [InlineData("")]
    [InlineData(" ")]
    public async Task AnInvitationIdThatIsNotOpaqueIsRefusedInBothDirections(string id)
    {
        var (service, boardId) = await LinkedBoardAsync();
        HostedInvitations(Guid.NewGuid().ToString("D"), id);

        var listed = await Assert.ThrowsAsync<BoardValidationException>(() => service.GetAsync(root, boardId, Ct));
        Assert.Contains("invalid collaborator list", listed.Message);

        await Assert.ThrowsAsync<BoardValidationException>(() => service.RemoveAsync(root, boardId, id, Ct));
        await Assert.ThrowsAsync<BoardValidationException>(() => service.SaveAsync(root, boardId, id, new("person@example.com"), Ct));
        client.Verify(c => c.RemoveInviteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<string?>()), Times.Never);
        client.Verify(c => c.SaveInviteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<BoardSharingEmailRequest>(), It.IsAny<CancellationToken>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task OpaqueIdsAndNewInvitationsStillReachTheHostedOwnerCheck()
    {
        var (service, boardId) = await LinkedBoardAsync();
        var id = Guid.NewGuid().ToString("D");

        await service.RemoveAsync(root, boardId, id, Ct);
        await service.SaveAsync(root, boardId, id, new("person@example.com"), Ct);
        await service.SaveAsync(root, boardId, null, new("new@example.com"), Ct);

        client.Verify(c => c.RemoveInviteAsync(remoteBoard, id, It.IsAny<CancellationToken>(), Destination), Times.Once);
        client.Verify(c => c.SaveInviteAsync(remoteBoard, id, It.IsAny<BoardSharingEmailRequest>(), It.IsAny<CancellationToken>(), Destination), Times.Once);
        client.Verify(c => c.SaveInviteAsync(remoteBoard, null, It.IsAny<BoardSharingEmailRequest>(), It.IsAny<CancellationToken>(), Destination), Times.Once);
    }
}
