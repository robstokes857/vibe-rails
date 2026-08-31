using Microsoft.Extensions.DependencyInjection;
using Moq;
using Serilog.Events;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services.Jobs;
using Xunit;

namespace Tests.Services.Jobs;

[Collection(JobSchedulerHostedServiceTestCollection.Name)]
public sealed class JobSchedulerHostedServiceTests
{
    [Fact]
    public async Task RunCycleAsync_WhenLeaseIsHeldElsewhere_DoesNotInspectOrDrainTheQueue()
    {
        var store = new Mock<IJobStore>(MockBehavior.Strict);
        store
            .Setup(candidate => candidate.TryAcquireOrRenewSchedulerLeaseAsync(
                It.IsAny<string>(),
                It.IsAny<DateTime>(),
                JobSchedulerHostedService.SchedulerLeaseDuration,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var launcher = new Mock<IJobLaunchService>(MockBehavior.Strict);

        await using var services = new ServiceCollection()
            .AddSingleton(launcher.Object)
            .BuildServiceProvider();
        var health = new JobSchedulerHealth();
        var scheduler = new JobSchedulerHostedService(
            services.GetRequiredService<IServiceScopeFactory>(),
            store.Object,
            health);

        var ran = await scheduler.RunCycleAsync(
            new DateTime(2026, 7, 26, 12, 0, 0, DateTimeKind.Utc),
            TestContext.Current.CancellationToken);

        Assert.False(ran);
        var snapshot = health.GetSnapshot();
        Assert.Equal(new DateTime(2026, 7, 26, 12, 0, 0, DateTimeKind.Utc), snapshot.LastCycleStartedUtc);
        Assert.NotNull(snapshot.LastCycleCompletedUtc);
        Assert.Null(snapshot.LastSuccessfulCycleUtc);
        Assert.False(snapshot.OwnsSchedulerLease);
        Assert.Null(snapshot.LastError);
        store.VerifyAll();
        store.VerifyNoOtherCalls();
        launcher.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RunCycleAsync_WhenLeaseIsAcquired_DrainsTheDurableQueue()
    {
        var nowUtc = new DateTime(2026, 7, 26, 12, 0, 0, DateTimeKind.Utc);
        var sequence = new MockSequence();
        var store = new Mock<IJobStore>(MockBehavior.Strict);
        var launcher = new Mock<IJobLaunchService>(MockBehavior.Strict);
        store.InSequence(sequence)
            .Setup(candidate => candidate.TryAcquireOrRenewSchedulerLeaseAsync(
                It.IsAny<string>(),
                nowUtc,
                JobSchedulerHostedService.SchedulerLeaseDuration,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        store.InSequence(sequence)
            .Setup(candidate => candidate.GetActiveRunsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<JobRunRecord>());
        store.InSequence(sequence)
            .Setup(candidate => candidate.FailStalledLaunchesAsync(
                It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(2);
        store.InSequence(sequence)
            .Setup(candidate => candidate.EnqueueDueSchedulesAsync(
                nowUtc,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(["due-1", "due-2", "due-3"]);
        launcher.InSequence(sequence)
            .Setup(candidate => candidate.LaunchQueuedRunsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        await using var services = new ServiceCollection()
            .AddSingleton(launcher.Object)
            .BuildServiceProvider();
        var health = new JobSchedulerHealth();
        var scheduler = new JobSchedulerHostedService(
            services.GetRequiredService<IServiceScopeFactory>(),
            store.Object,
            health);

        Assert.True(await scheduler.RunCycleAsync(nowUtc, TestContext.Current.CancellationToken));
        var snapshot = health.GetSnapshot();
        Assert.NotNull(snapshot.LastSuccessfulCycleUtc);
        Assert.True(snapshot.OwnsSchedulerLease);
        Assert.Equal(3, snapshot.LastSchedulesEnqueued);
        Assert.Equal(1, snapshot.LastRunsLaunched);
        Assert.Equal(0, snapshot.LastRunsReaped);
        Assert.Equal(2, snapshot.LastStalledLaunchesFailed);
        Assert.Null(snapshot.LastError);
        store.VerifyAll();
        launcher.VerifyAll();
    }

    [Theory]
    [InlineData(0, 0, 0, 0, LogEventLevel.Debug)]
    [InlineData(1, 0, 0, 0, LogEventLevel.Information)]
    [InlineData(0, 1, 0, 0, LogEventLevel.Information)]
    [InlineData(0, 0, 1, 0, LogEventLevel.Information)]
    [InlineData(0, 0, 0, 1, LogEventLevel.Information)]
    public void GetCycleCompletionLogLevel_UsesInformationOnlyForMeaningfulWork(
        int schedulesEnqueued,
        int runsLaunched,
        int runsReaped,
        int stalledLaunchesFailed,
        LogEventLevel expected)
    {
        Assert.Equal(
            expected,
            JobSchedulerHostedService.GetCycleCompletionLogLevel(
                schedulesEnqueued,
                runsLaunched,
                runsReaped,
                stalledLaunchesFailed));
    }

    [Fact]
    public async Task RunCycleAsync_RenewsLeaseWhileLaunchBatchIsStillRunning()
    {
        var renewalObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLaunch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var leaseCalls = 0;

        var store = new Mock<IJobStore>(MockBehavior.Strict);
        store
            .Setup(candidate => candidate.TryAcquireOrRenewSchedulerLeaseAsync(
                It.IsAny<string>(),
                It.IsAny<DateTime>(),
                JobSchedulerHostedService.SchedulerLeaseDuration,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                if (Interlocked.Increment(ref leaseCalls) >= 2)
                    renewalObserved.TrySetResult();
                return true;
            });
        store
            .Setup(candidate => candidate.GetActiveRunsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<JobRunRecord>());
        store
            .Setup(candidate => candidate.FailStalledLaunchesAsync(
                It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);
        store
            .Setup(candidate => candidate.EnqueueDueSchedulesAsync(
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<string>());

        var launcher = new Mock<IJobLaunchService>(MockBehavior.Strict);
        launcher
            .Setup(candidate => candidate.LaunchQueuedRunsAsync(It.IsAny<CancellationToken>()))
            .Returns<CancellationToken>(async cancellationToken =>
            {
                await releaseLaunch.Task.WaitAsync(cancellationToken);
                return 0;
            });

        await using var services = new ServiceCollection()
            .AddSingleton(launcher.Object)
            .BuildServiceProvider();
        var health = new JobSchedulerHealth();
        var scheduler = new JobSchedulerHostedService(
            services.GetRequiredService<IServiceScopeFactory>(),
            store.Object,
            health)
        {
            LeaseRenewalInterval = TimeSpan.FromMilliseconds(10)
        };

        var cycle = scheduler.RunCycleAsync(
            new DateTime(2026, 7, 26, 12, 0, 0, DateTimeKind.Utc),
            TestContext.Current.CancellationToken);
        await renewalObserved.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        releaseLaunch.TrySetResult();

        Assert.True(await cycle);
        Assert.True(Volatile.Read(ref leaseCalls) >= 2);
        store.VerifyAll();
        launcher.VerifyAll();
    }

    [Fact]
    public async Task RunCycleAsync_WhenLeaseRenewalIsLost_CancelsRemainingLaunches()
    {
        var leaseCalls = 0;
        var health = new JobSchedulerHealth();
        var launchCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new Mock<IJobStore>(MockBehavior.Strict);
        store
            .Setup(candidate => candidate.TryAcquireOrRenewSchedulerLeaseAsync(
                It.IsAny<string>(),
                It.IsAny<DateTime>(),
                JobSchedulerHostedService.SchedulerLeaseDuration,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => Interlocked.Increment(ref leaseCalls) == 1);
        store
            .Setup(candidate => candidate.GetActiveRunsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<JobRunRecord>());
        store
            .Setup(candidate => candidate.FailStalledLaunchesAsync(
                It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);
        store
            .Setup(candidate => candidate.EnqueueDueSchedulesAsync(
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<string>());

        var launcher = new Mock<IJobLaunchService>(MockBehavior.Strict);
        launcher
            .Setup(candidate => candidate.LaunchQueuedRunsAsync(It.IsAny<CancellationToken>()))
            .Returns<CancellationToken>(async cancellationToken =>
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    return 0;
                }
                catch (OperationCanceledException)
                {
                    launchCancelled.TrySetResult();
                    throw;
                }
            });

        await using var services = new ServiceCollection()
            .AddSingleton(launcher.Object)
            .BuildServiceProvider();
        var scheduler = new JobSchedulerHostedService(
            services.GetRequiredService<IServiceScopeFactory>(),
            store.Object,
            health)
        {
            LeaseRenewalInterval = TimeSpan.FromMilliseconds(10)
        };

        Assert.False(await scheduler.RunCycleAsync(
            new DateTime(2026, 7, 26, 12, 0, 0, DateTimeKind.Utc),
            TestContext.Current.CancellationToken));
        await launchCancelled.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, Volatile.Read(ref leaseCalls));
        Assert.False(health.GetSnapshot().OwnsSchedulerLease);
        store.VerifyAll();
        launcher.VerifyAll();
    }

    [Fact]
    public async Task ExecuteAsync_CoalescesWakeBursts_AndReleasesItsLeaseOnStop()
    {
        var firstLaunchStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowFirstLaunchToFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondLaunchStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var launchCount = 0;
        string? leaseOwner = null;

        var store = new Mock<IJobStore>(MockBehavior.Strict);
        store
            .Setup(candidate => candidate.TryAcquireOrRenewSchedulerLeaseAsync(
                It.IsAny<string>(),
                It.IsAny<DateTime>(),
                JobSchedulerHostedService.SchedulerLeaseDuration,
                It.IsAny<CancellationToken>()))
            .Callback<string, DateTime, TimeSpan, CancellationToken>(
                (owner, _, _, _) => leaseOwner ??= owner)
            .ReturnsAsync(true);
        store
            .Setup(candidate => candidate.GetActiveRunsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<JobRunRecord>());
        store
            .Setup(candidate => candidate.FailStalledLaunchesAsync(
                It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);
        store
            .Setup(candidate => candidate.EnqueueDueSchedulesAsync(
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<string>());
        store
            .Setup(candidate => candidate.ReleaseSchedulerLeaseAsync(
                It.Is<string>(owner => owner == leaseOwner),
                CancellationToken.None))
            .ReturnsAsync(true);

        var launcher = new Mock<IJobLaunchService>(MockBehavior.Strict);
        launcher
            .Setup(candidate => candidate.LaunchQueuedRunsAsync(It.IsAny<CancellationToken>()))
            .Returns<CancellationToken>(async cancellationToken =>
            {
                var current = Interlocked.Increment(ref launchCount);
                if (current == 1)
                {
                    firstLaunchStarted.TrySetResult();
                    await allowFirstLaunchToFinish.Task.WaitAsync(cancellationToken);
                }
                else if (current == 2)
                {
                    secondLaunchStarted.TrySetResult();
                }
                return 0;
            });

        await using var services = new ServiceCollection()
            .AddSingleton(launcher.Object)
            .BuildServiceProvider();
        var health = new JobSchedulerHealth();
        var scheduler = new JobSchedulerHostedService(
            services.GetRequiredService<IServiceScopeFactory>(),
            store.Object,
            health);

        // Use the seam instead of flipping the process-global parsed-args state.
        scheduler.IsBootstrapProcess = static () => false;
        var started = false;
        var stopped = false;
        try
        {
            await scheduler.StartAsync(TestContext.Current.CancellationToken);
            started = true;

            await firstLaunchStarted.Task.WaitAsync(
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken);
            for (var index = 0; index < 50; index++)
                scheduler.Kick();

            allowFirstLaunchToFinish.TrySetResult();
            await secondLaunchStarted.Task.WaitAsync(
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken);

            using var stopTimeout = CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken);
            stopTimeout.CancelAfter(TimeSpan.FromSeconds(5));
            await scheduler.StopAsync(stopTimeout.Token);
            stopped = true;

            Assert.Equal(2, Volatile.Read(ref launchCount));
            Assert.False(string.IsNullOrWhiteSpace(leaseOwner));
            Assert.False(health.GetSnapshot().OwnsSchedulerLease);
            store.Verify(candidate => candidate.ReleaseSchedulerLeaseAsync(
                leaseOwner!,
                CancellationToken.None), Times.Once);
        }
        finally
        {
            allowFirstLaunchToFinish.TrySetResult();
            if (started && !stopped)
            {
                using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await scheduler.StopAsync(cleanupTimeout.Token); }
                catch { /* best-effort test cleanup */ }
            }
        }
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class JobSchedulerHostedServiceTestCollection
{
    public const string Name = "Job scheduler hosted service";
}
