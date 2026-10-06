using Moq;
using VibeRails.DTOs;
using VibeRails.Services.Board;
using VibeRails.Services.Board.Sync;
using Xunit;

namespace Tests.Services.Board;

public sealed class BoardRemoteLaunchTests
{
    private readonly Mock<IBoardStore> store = new(MockBehavior.Strict);
    private readonly Mock<IBoardSyncClient> client = new();
    private readonly Mock<IBoardSyncService> sync = new(MockBehavior.Strict);
    private readonly Mock<IBoardLaunchService> launch = new(MockBehavior.Strict);
    private readonly Guid remote = Guid.NewGuid();
    private readonly Guid instance = Guid.NewGuid();
    private const string Project = "C:/project";
    private const string Destination = "configured-account";
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SyncsSavedCardBeforeLaunchingWithNoOverrides()
    {
        Setup();
        launch.Setup(x => x.LaunchAsync(Project, "card", null, Ct, "work"))
            .ReturnsAsync(new LaunchBoardCardResponse("tab", Guid.NewGuid().ToString(), "codex", Project, "card", "VB-1", "codex"));
        var result = await Run();
        Assert.Equal("started", result.Status);
        Assert.NotNull(result.SessionId);
        sync.Verify(x => x.SyncNowAsync(Project, "board", Ct), Times.Once);
        launch.VerifyAll();
    }

    [Theory]
    [InlineData("imported")]
    [InlineData("disabled")]
    [InlineData("destination")]
    [InlineData("wrong_board")]
    [InlineData("missing_card")]
    [InlineData("changed_destination")]
    [InlineData("changed_link")]
    [InlineData("behind")]
    [InlineData("sync_error")]
    [InlineData("unsent")]
    [InlineData("unapplied")]
    public async Task RefusesUntrustedOrStaleLaunchTargets(string failure)
    {
        Setup(failure);
        Assert.NotEqual("started", (await Run()).Status);
        launch.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ReportsExistingAgentConflict()
    {
        Setup();
        launch.Setup(x => x.LaunchAsync(Project, "card", null, Ct, "work"))
            .ThrowsAsync(new BoardConflictException("Already running"));
        Assert.Equal("busy", (await Run()).Status);
    }

    private Task<BoardLaunchResult> Run() => new BoardRemoteLaunchService(store.Object, client.Object, sync.Object, launch.Object)
        .LaunchAsync(Project, instance, new(Guid.NewGuid(), remote, "card", 12), Destination, Ct);

    private void Setup(string? failure = null)
    {
        client.SetupGet(x => x.DestinationKey).Returns(Destination);
        store.Setup(x => x.IsCardSyncAppliedAsync(Project, "board", "card", 12, Ct)).ReturnsAsync(failure != "unapplied");
        var board = new BoardRecord("board", Project, "Project", 0, default, default, SyncEnabled: failure != "disabled");
        store.Setup(x => x.GetBoardsAsync(Project, Ct)).ReturnsAsync([board]);
        store.Setup(x => x.GetBoardAsync(Project, "board", Ct)).ReturnsAsync(board);
        var link = new BoardSyncLinkRecord("board", remote.ToString(), 12, true, null, null, null, default, default,
            Project, "Project", failure == "destination" ? "other" : Destination, Imported: failure == "imported");
        var linkReads = 0;
        store.Setup(x => x.GetSyncLinkAsync(Project, "board", Ct)).ReturnsAsync(() =>
            failure == "changed_link" && ++linkReads > 1 ? link with { RemoteBoardId = Guid.NewGuid().ToString() } : link);
        sync.Setup(x => x.SyncNowAsync(Project, "board", Ct)).ReturnsAsync(() =>
        {
            if (failure == "changed_destination") client.SetupGet(x => x.DestinationKey).Returns("other-account");
            return new BoardSyncStatus("board", true, true, remote.ToString(), null, failure == "behind" ? 11 : 12,
                failure == "unsent" ? 1 : 0, null, failure == "sync_error" ? "Failed" : null, true);
        });
        store.Setup(x => x.FindCardAsync(Project, "card", Ct)).ReturnsAsync(failure == "missing_card" ? null :
            new BoardCardRecord("card", Project, 1, "lane", 0, "Task", "Description", "codex", "medium", null, [], false, 0,
                default, default, BoardId: failure == "wrong_board" ? "another-board" : "board"));
    }
}
