using Microsoft.Extensions.DependencyInjection;
using Moq;
using Tests.Services.Integrations;
using VibeRails.DTOs;
using VibeRails.Services.Board;
using VibeRails.Services.Board.Sync;
using VibeRails.Services.Integrations.VibeCodeRemote;
using Xunit;

namespace Tests.Services.Board;

/// <summary>
/// The ten-second remote Start work poll is the busiest loop a downgraded account has. It must
/// skip while the plan says no, and stop after one refused poll instead of asking every turn.
/// </summary>
public sealed class BoardRemoteLaunchPollTests
{
    private const string Project = "C:/project";
    private const string Destination = "configured-account";
    private readonly Mock<IBoardStore> store = new();
    private readonly Mock<IBoardSyncClient> client = new();
    private readonly FakeAccountEntitlements entitlements = new();
    private readonly Guid remote = Guid.NewGuid();
    private readonly Mock<IBoardSyncService> sync = new();
    private readonly Mock<IBoardLaunchService> launch = new();
    private int polls;
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ThePollIsSkippedWhileThePlanDoesNotIncludeRemoteBoards_AndResumesWhenItDoes()
    {
        using var provider = Setup();
        var service = Service(provider);
        entitlements.Off.Add(ProFeatures.RemoteBoards);

        for (var turn = 0; turn < 3; turn++)
            await service.PollOnceAsync(Ct);
        Assert.Equal(0, polls);

        entitlements.Off.Clear();
        await service.PollOnceAsync(Ct);
        Assert.Equal(1, polls);
    }

    [Fact]
    public async Task ARefusedPollIsNotRepeated()
    {
        using var provider = Setup(poll: () => throw BoardSyncProPause.Failure(new ProDenial(ProFeatures.RemoteBoards, ProDenial.AccountPayer)));
        var service = Service(provider);

        for (var turn = 0; turn < 4; turn++)
            await service.PollOnceAsync(Ct); // never throws: the loop keeps its ten-second rhythm

        Assert.Equal(1, polls);
        Assert.Equal([ProFeatures.RemoteBoards], entitlements.Noted);
    }

    [Fact]
    public async Task ADenialThatIsNotThisAccountsWaitsInMemoryInsteadOfAskingEveryTurn()
    {
        using var provider = Setup(poll: () => throw BoardSyncProPause.Failure(new ProDenial(ProFeatures.RemoteBoards, ProDenial.BoardOwnerPayer)));
        var service = Service(provider);

        for (var turn = 0; turn < 4; turn++)
            await service.PollOnceAsync(Ct);

        Assert.Equal(1, polls);
        Assert.Empty(entitlements.Noted); // this account's own plan is never marked
    }

    [Fact]
    public async Task ThePlanIsNotAskedWhenNoBoardIsAdvertised_AndOtherFailuresStillPollNextTurn()
    {
        using var none = Setup(published: false);
        await Service(none).PollOnceAsync(Ct);
        Assert.Equal((0, 0), (polls, entitlements.Reads));

        using var failing = Setup(poll: () => throw new BoardSyncClientException("viberails.ai could not be reached.", "network"));
        var service = Service(failing);
        await service.PollOnceAsync(Ct);
        await service.PollOnceAsync(Ct);
        Assert.Equal(2, polls); // an outage is not a plan answer: unchanged behaviour
    }

    [Theory]
    [InlineData("sync")]
    [InlineData("launch")]
    [InlineData("result")]
    public async Task SlowWorkKeepsHeartbeatsAliveAndNeverStartsASecondWorker(string slowStage)
    {
        using var provider = Setup();
        var service = Service(provider);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var command = new BoardLaunchCommand(Guid.NewGuid(), remote, "card", 12);
        var sent = new List<BoardLaunchPoll>();
        async Task Pause(string stage, CancellationToken ct)
        {
            if (slowStage != stage) return;
            entered.SetResult();
            await release.Task.WaitAsync(ct);
        }
        client.Setup(x => x.PollLaunchAsync(It.IsAny<BoardLaunchPoll>(), It.IsAny<CancellationToken>(), Destination))
            .Returns((BoardLaunchPoll poll, CancellationToken _, string _) =>
            {
                sent.Add(poll);
                return Task.FromResult(new BoardLaunchPollResult(sent.Count == 1 ? command : null));
            });
        sync.Setup(x => x.SyncNowAsync(Project, "board", It.IsAny<CancellationToken>()))
            .Returns(async (string _, string _, CancellationToken ct) =>
            {
                await Pause("sync", ct);
                return new BoardSyncStatus("board", true, true, remote.ToString(), null, 12, 0, null, null, true);
            });
        store.Setup(x => x.GetBoardAsync(Project, "board", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BoardRecord("board", Project, "Project", 0, default, default, SyncEnabled: true));
        store.Setup(x => x.IsCardSyncAppliedAsync(Project, "board", "card", 12, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        store.Setup(x => x.FindCardAsync(Project, "card", It.IsAny<CancellationToken>())).ReturnsAsync(
            new BoardCardRecord("card", Project, 1, "lane", 0, "Task", "Description", "codex", "medium", null, [], false, 0,
                default, default, BoardId: "board"));
        launch.Setup(x => x.LaunchAsync(Project, "card", null, It.IsAny<CancellationToken>(), "work"))
            .Returns(async () =>
            {
                await Pause("launch", Ct);
                return new LaunchBoardCardResponse("tab", Guid.NewGuid().ToString(), "codex", Project, "card", "VB-1", "codex");
            });
        client.Setup(x => x.CompleteLaunchAsync(command.Id, It.IsAny<BoardLaunchResult>(), It.IsAny<CancellationToken>(), Destination))
            .Returns(async (Guid _, BoardLaunchResult _, CancellationToken ct, string _) =>
            {
                await Pause("result", ct);
                return new BoardLaunchResultAck(true);
            });
        try
        {
            await service.PollOnceAsync(Ct).WaitAsync(TimeSpan.FromSeconds(5), Ct);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
            for (var turn = 0; turn < 4; turn++) await service.PollOnceAsync(Ct);
            Assert.True(sent[0].AcceptLaunch);
            Assert.All(sent.Skip(1), poll =>
            {
                Assert.False(poll.AcceptLaunch);
                Assert.Equal([remote], poll.BoardIds);
                Assert.Equal(sent[0].InstanceId, poll.InstanceId);
            });
        }
        finally
        {
            release.TrySetResult();
            await service.WaitForLaunchAsync().WaitAsync(TimeSpan.FromSeconds(5), Ct);
        }
        await service.PollOnceAsync(Ct);
        Assert.True(sent[^1].AcceptLaunch);
        launch.Verify(x => x.LaunchAsync(Project, "card", null, It.IsAny<CancellationToken>(), "work"), Times.Once);
        client.Verify(x => x.CompleteLaunchAsync(command.Id, It.Is<BoardLaunchResult>(r => r.Status == "started"),
            It.IsAny<CancellationToken>(), Destination), Times.Once);
    }

    [Fact]
    public async Task IneligibleBoardsDoNotUseUpTheAdvertisementLimit()
    {
        using var provider = Setup();
        var boards = Enumerable.Range(0, 100).Select(i =>
            new BoardRecord("disabled-" + i, Project, "Disabled", i, default, default, SyncEnabled: false)).ToList();
        boards.Add(new BoardRecord("board", Project, "Project", 100, default, default, SyncEnabled: true));
        store.Setup(x => x.GetBoardsAsync(Project, It.IsAny<CancellationToken>())).ReturnsAsync(boards);
        await Service(provider).PollOnceAsync(Ct);
        client.Verify(x => x.PollLaunchAsync(It.Is<BoardLaunchPoll>(p => p.BoardIds.Count == 1 && p.BoardIds[0] == remote),
            It.IsAny<CancellationToken>(), Destination), Times.Once);
    }

    private BoardRemoteLaunchHostedService Service(ServiceProvider provider) =>
        new(provider.GetRequiredService<IServiceScopeFactory>(), client.Object, entitlements);

    private ServiceProvider Setup(bool published = true, Action? poll = null)
    {
        client.SetupGet(x => x.IsConfigured).Returns(true);
        client.SetupGet(x => x.DestinationKey).Returns(Destination);
        client.Setup(x => x.PollLaunchAsync(It.IsAny<BoardLaunchPoll>(), It.IsAny<CancellationToken>(), Destination))
            .Returns(() =>
            {
                polls++;
                poll?.Invoke();
                return Task.FromResult(new BoardLaunchPollResult(null));
            });
        var board = new BoardRecord("board", Project, "Project", 0, default, default, SyncEnabled: published);
        store.Setup(x => x.GetBoardsAsync(Project, It.IsAny<CancellationToken>())).ReturnsAsync([board]);
        store.Setup(x => x.GetSyncLinkAsync(Project, "board", It.IsAny<CancellationToken>())).ReturnsAsync(
            new BoardSyncLinkRecord("board", remote.ToString(), 12, true, null, null, null, default, default, Project, "Project", Destination));
        var projects = new Mock<IBoardProjectResolver>();
        projects.Setup(x => x.ResolveAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Project);
        var services = new ServiceCollection();
        services.AddSingleton(store.Object);
        services.AddSingleton(projects.Object);
        services.AddSingleton(client.Object);
        services.AddSingleton(sync.Object);
        services.AddSingleton(launch.Object);
        services.AddScoped<BoardRemoteLaunchService>();
        return services.BuildServiceProvider();
    }
}
