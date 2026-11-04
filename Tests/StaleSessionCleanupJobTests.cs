using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using VibeRails.DB;
using VibeRails.DTOs;
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
        var dbService = new Mock<IRepository>();
        dbService
            .Setup(x => x.GetOpenSessionCleanupCandidatesAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<OpenSessionCleanupCandidate>
            {
                new("session-1", int.MaxValue, DateTime.UtcNow),
                new("session-2", int.MaxValue - 1, DateTime.UtcNow)
            });

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
        var dbService = new Mock<IRepository>();
        dbService
            .Setup(x => x.GetOpenSessionCleanupCandidatesAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<OpenSessionCleanupCandidate>());

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

    [Fact]
    public async Task ExecuteJob_ClosesLegacySessionsWithNullOwnerPid()
    {
        var dbService = new Mock<IRepository>();
        dbService
            .Setup(x => x.GetOpenSessionCleanupCandidatesAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<OpenSessionCleanupCandidate>
            {
                new("legacy-session", null, DateTime.UtcNow.AddHours(-2))
            });

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

        dbService.Verify(x => x.CompleteSessionAsync("legacy-session", -1), Times.Once);
        remoteStateService.Verify(x => x.DeregisterTerminalAsync("legacy-session"), Times.Once);
    }

    [Fact]
    public async Task ExecuteJob_DoesNotCloseSessionsOwnedByLiveProcess()
    {
        var dbService = new Mock<IRepository>();
        dbService
            .Setup(x => x.GetOpenSessionCleanupCandidatesAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<OpenSessionCleanupCandidate>
            {
                new("session-1", Environment.ProcessId, ProcessStartUtc().AddMinutes(1))
            });

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

    /// <summary>
    /// The owner PID is alive but belongs to a process that started after the session was recorded:
    /// the operating system recycled the PID, so nothing is running the session and it closes.
    /// On macOS this is also the row behind the repeated Process.StartTime first-chance warnings:
    /// a recycled PID owned by root cannot be inspected, and the old "has it exited" check kept it.
    /// </summary>
    [Fact]
    public async Task ExecuteJob_ClosesSessionsWhoseOwnerPidWasRecycled()
    {
        var dbService = new Mock<IRepository>();
        dbService
            .Setup(x => x.GetOpenSessionCleanupCandidatesAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<OpenSessionCleanupCandidate>
            {
                new("recycled-session", Environment.ProcessId, ProcessStartUtc().AddMinutes(-10))
            });

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

        dbService.Verify(x => x.CompleteSessionAsync("recycled-session", -1), Times.Once);
        remoteStateService.Verify(x => x.DeregisterTerminalAsync("recycled-session"), Times.Once);
    }

    private static DateTime ProcessStartUtc()
    {
        using var self = Process.GetCurrentProcess();
        return self.StartTime.ToUniversalTime();
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
