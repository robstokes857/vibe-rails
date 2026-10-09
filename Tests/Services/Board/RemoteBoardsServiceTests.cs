using Moq;
using VibeRails.Services.Board;
using VibeRails.Services.Board.Sync;
using Xunit;

namespace Tests.Services.Board;

public sealed class RemoteBoardsServiceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "remote-boards-" + Guid.NewGuid().ToString("N"));
    private readonly BoardStore store;
    private readonly Mock<IBoardSyncClient> client = new(MockBehavior.Strict);
    private readonly BoardSyncLock syncLock;
    private readonly RemoteBoardsService service;
    private readonly string remoteId = Guid.NewGuid().ToString("D");
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public RemoteBoardsServiceTests()
    {
        Directory.CreateDirectory(root);
        var cs = $"Data Source={Path.Combine(root, "board.db")};Pooling=False";
        store = new BoardStore(cs, cs);
        syncLock = new(Path.Combine(root, "sync.lock"));
        service = new(store, client.Object, syncLock);
        client.SetupGet(c => c.DestinationKey).Returns("account-a");
        client.Setup(c => c.DiscoverPageAsync(0, It.IsAny<CancellationToken>(), "account-a"))
            .ReturnsAsync(() => [Descriptor()]);
        client.Setup(c => c.DescribeAsync(remoteId, It.IsAny<CancellationToken>(), "account-a"))
            .ReturnsAsync(() => Descriptor());
    }

    private BoardRemoteDescriptor Descriptor(bool owner = true) => new(remoteId, "Remote", "VB", "VB", [], owner, 1);
    private async Task<string> Context() => (await service.ListAsync(0, Ct)).Context;
    private async Task<BoardRecord> Local(string project, string destination = "account-a")
    {
        var board = await store.CreateBoardAsync(Path.Combine(root, project), "Local", Ct);
        await store.SetBoardSyncEnabledAsync(board.ProjectPath, board.Id, true, Ct);
        await store.SaveSyncLinkAsync(new(board.Id, remoteId, 0, true, null, null, null, DateTime.UtcNow, DateTime.UtcNow,
            board.ProjectPath, board.Name, destination, 1), Ct);
        return board;
    }

    [Fact]
    public async Task ListingMatchesCopiesAcrossProjectsOnlyForCurrentAccountAndPages()
    {
        var first = await Local("one");
        var second = await Local("two");
        await Local("other-account", "account-b");
        client.Setup(c => c.DiscoverPageAsync(100, It.IsAny<CancellationToken>(), "account-a"))
            .ReturnsAsync(Enumerable.Range(0, 100).Select(_ => Descriptor()).ToList());
        var page = await service.ListAsync(0, Ct);
        Assert.Null(page.NextOffset);
        var row = Assert.Single(page.Boards);
        Assert.Equal(new[] { first.Id, second.Id }.Order(), row.LocalCopies.Select(b => b.BoardId).Order());
        Assert.Equal(new[] { "one", "two" }, row.LocalCopies.Select(b => b.ProjectName).Order());
        Assert.Equal(200, (await service.ListAsync(100, Ct)).NextOffset);
        await Assert.ThrowsAsync<BoardValidationException>(() => service.ListAsync(-100, Ct));
        await Assert.ThrowsAsync<BoardValidationException>(() => service.ListAsync(1, Ct));
    }

    [Fact]
    public async Task CreateRetriesUseSameRemoteIdentityWithoutCreatingLocalData()
    {
        var sent = new List<BoardSyncPublishRequest>();
        client.Setup(c => c.PublishAsync(It.IsAny<BoardSyncPublishRequest>(), It.IsAny<CancellationToken>(), "account-a"))
            .Callback<BoardSyncPublishRequest, CancellationToken, string?>((r, _, _) => sent.Add(r))
            .ReturnsAsync(new BoardSyncPublishResponse(Guid.Parse(remoteId), "New", 0));
        var request = new RemoteBoardWriteRequest(await Context(), " New ", new string('a', 32));
        Assert.Equal(remoteId, (await service.CreateAsync(request, Ct)).Id);
        await service.CreateAsync(request, Ct);
        Assert.Equal(sent[0].LocalBoardId, sent[1].LocalBoardId);
        Assert.Equal("New", sent[0].Name);
        Assert.Equal(5, sent[0].Lanes.Count);
        Assert.Empty(await store.GetLocalBoardsAsync(Ct));
        await Assert.ThrowsAsync<BoardValidationException>(() => service.CreateAsync(request with { RequestId = "invalid" }, Ct));
        await Assert.ThrowsAsync<BoardValidationException>(() => service.CreateAsync(request with { Name = "two\nlines" }, Ct));
    }

    [Fact]
    public async Task RenameUpdatesRemoteAndMatchingLocalNamesWithoutChangingCardsOrLanes()
    {
        var local = await Local("one");
        var foreign = await Local("foreign", "account-b");
        client.Setup(c => c.PushAsync(remoteId, It.Is<BoardSyncPushRequest>(r => r.Name == "Renamed" && r.Lanes == null && r.Entries.Count == 0),
            It.IsAny<CancellationToken>(), "account-a")).ReturnsAsync(new BoardSyncPushResponse([], 0));
        await service.RenameAsync(remoteId, new(await Context(), "Renamed"), Ct);
        Assert.Equal("Renamed", (await store.GetBoardAsync(local.ProjectPath, local.Id, Ct))!.Name);
        Assert.Equal("Local", (await store.GetBoardAsync(foreign.ProjectPath, foreign.Id, Ct))!.Name);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeletePausesBeforeSendingAndKeepsLocalCardsEvenWhenResponseIsLost(bool fail)
    {
        var local = await Local("one");
        var foreign = await Local("foreign", "account-b");
        var card = await store.CreateCardAsync(local.ProjectPath, new(null, "Keep me", "", null, "medium", null, [], false, BoardId: local.Id), Ct);
        client.Setup(c => c.DeleteRemoteBoardAsync(remoteId, It.IsAny<CancellationToken>(), "account-a"))
            .Returns(async () =>
            {
                Assert.False((await store.GetBoardAsync(local.ProjectPath, local.Id, Ct))!.SyncEnabled);
                if (fail) throw new BoardSyncClientException("Secret remote error", "network");
            });
        var request = new RemoteBoardWriteRequest(await Context());
        if (fail)
        {
            var error = await Assert.ThrowsAsync<BoardValidationException>(() => service.DeleteAsync(remoteId, request, Ct));
            Assert.Contains("not confirmed", error.Message);
            Assert.DoesNotContain("Secret", error.Message);
        }
        else await service.DeleteAsync(remoteId, request, Ct);
        Assert.False((await store.GetBoardAsync(local.ProjectPath, local.Id, Ct))!.SyncEnabled);
        Assert.True((await store.GetBoardAsync(foreign.ProjectPath, foreign.Id, Ct))!.SyncEnabled);
        Assert.NotNull(await store.FindCardAsync(local.ProjectPath, card.Id, Ct));
        Assert.NotNull(await store.GetSyncLinkAsync(local.ProjectPath, local.Id, Ct));
    }

    [Fact]
    public async Task SharedBoardsCannotBeRenamedOrDeletedAndLocalSyncIsUntouched()
    {
        var local = await Local("one");
        client.Setup(c => c.DescribeAsync(remoteId, It.IsAny<CancellationToken>(), "account-a")).ReturnsAsync(Descriptor(false));
        var request = new RemoteBoardWriteRequest(await Context(), "New");
        await Assert.ThrowsAsync<BoardValidationException>(() => service.DeleteAsync(remoteId, request, Ct));
        await Assert.ThrowsAsync<BoardValidationException>(() => service.RenameAsync(remoteId, request, Ct));
        Assert.True((await store.GetBoardAsync(local.ProjectPath, local.Id, Ct))!.SyncEnabled);
    }

    [Fact]
    public async Task AccountChangesAndConcurrentSyncRejectWritesBeforeSideEffects()
    {
        var request = new RemoteBoardWriteRequest(await Context(), "New", new string('b', 32));
        using (var held = syncLock.TryAcquire())
        {
            Assert.NotNull(held);
            await Assert.ThrowsAsync<BoardValidationException>(() => service.DeleteAsync(remoteId, request, Ct));
            await Assert.ThrowsAsync<BoardValidationException>(() => service.RenameAsync(remoteId, request, Ct));
        }
        client.SetupGet(c => c.DestinationKey).Returns("account-b");
        await Assert.ThrowsAsync<BoardValidationException>(() => service.DeleteAsync(remoteId, request, Ct));
        await Assert.ThrowsAsync<BoardValidationException>(() => service.RenameAsync(remoteId, request, Ct));
        await Assert.ThrowsAsync<BoardValidationException>(() => service.CreateAsync(request, Ct));
        client.Verify(c => c.DescribeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<string>()), Times.Never);
    }

    public void Dispose() => Directory.Delete(root, true);
}
