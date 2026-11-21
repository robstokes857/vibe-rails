using Microsoft.Extensions.DependencyInjection;
using Moq;
using Tests.Services.Integrations;
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
        return services.BuildServiceProvider();
    }
}
