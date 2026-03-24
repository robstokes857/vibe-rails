using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using VibeRails.Interfaces;
using VibeRails.Jobs;
using VibeRails.Services;
using VibeRails.Services.Integrations.VibeCodeRemote;
using VibeRails.Services.Terminal;
using Xunit;

namespace Tests;

public class StaleSessionCleanupJobTests
{
    [Fact]
    public async Task ExecuteJob_CompletesAndDeregistersEachStaleSession()
    {
        var dbService = new Mock<IDbService>();
        dbService
            .Setup(x => x.GetOpenSessionIdsAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string> { "session-1", "session-2" });

        using var serviceProvider = new ServiceCollection()
            .AddScoped(_ => dbService.Object)
            .BuildServiceProvider();

        var remoteStateService = new Mock<IRemoteStateService>();
        var job = new StaleSessionCleanupJob(
            NullLogger<StaleSessionCleanupJob>.Instance,
            Mock.Of<ISystemResourceService>(),
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            remoteStateService.Object);

        await InvokeExecuteJobAsync(job);

        dbService.Verify(x => x.CompleteSessionAsync("session-1", -1), Times.Once);
        dbService.Verify(x => x.CompleteSessionAsync("session-2", -1), Times.Once);
        remoteStateService.Verify(x => x.DeregisterTerminalAsync("session-1"), Times.Once);
        remoteStateService.Verify(x => x.DeregisterTerminalAsync("session-2"), Times.Once);
    }

    [Fact]
    public async Task ExecuteJob_DoesNothingWhenNoStaleSessionsExist()
    {
        var dbService = new Mock<IDbService>();
        dbService
            .Setup(x => x.GetOpenSessionIdsAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string>());

        using var serviceProvider = new ServiceCollection()
            .AddScoped(_ => dbService.Object)
            .BuildServiceProvider();

        var remoteStateService = new Mock<IRemoteStateService>();
        var job = new StaleSessionCleanupJob(
            NullLogger<StaleSessionCleanupJob>.Instance,
            Mock.Of<ISystemResourceService>(),
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            remoteStateService.Object);

        await InvokeExecuteJobAsync(job);

        dbService.Verify(x => x.CompleteSessionAsync(It.IsAny<string>(), It.IsAny<int>()), Times.Never);
        remoteStateService.Verify(x => x.DeregisterTerminalAsync(It.IsAny<string>()), Times.Never);
    }

    private static async Task InvokeExecuteJobAsync(StaleSessionCleanupJob job)
    {
        var executeJob = typeof(StaleSessionCleanupJob).GetMethod(
            "ExecuteJob",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(executeJob);

        var task = executeJob!.Invoke(job, [CancellationToken.None]) as Task;
        Assert.NotNull(task);

        await task!;
    }
}
