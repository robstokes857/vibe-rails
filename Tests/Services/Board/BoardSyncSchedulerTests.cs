using Microsoft.Extensions.DependencyInjection;
using Moq;
using VibeRails;
using VibeRails.Services.Board.Sync;
using Xunit;

namespace Tests.Services.Board;

/// <summary>
/// The 60-second Board sync tick (VB-51), shaped like the Jira pull scheduler: a singleton that
/// starts one sync from a fresh scope and never waits for it, overlaps it, or repeats it early.
/// </summary>
public sealed class BoardSyncSchedulerTests
{
    private static readonly DateTime Start = new(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Interval_IsSixtySeconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(60), BoardSyncScheduler.Interval);
    }

    [Fact]
    public void RealRegistration_ResolvesTheSingletonSchedulerUnderScopeValidation()
    {
        var services = new ServiceCollection();
        MapRegisterServices.Register(services, [], "http://127.0.0.1:12345");
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        Assert.IsType<BoardSyncScheduler>(provider.GetRequiredService<IBoardSyncScheduler>());
    }

    [Fact]
    public async Task Tick_StartsOneSyncFromAFreshScope_AndNeverOverlapsOrRepeatsWithinTheInterval()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var syncs = 0;
        var scopes = 0;
        using var provider = Provider(
            syncDue: () =>
            {
                Interlocked.Increment(ref syncs);
                return release.Task;
            },
            resolved: () => Interlocked.Increment(ref scopes));
        var scheduler = new BoardSyncScheduler(provider.GetRequiredService<IServiceScopeFactory>());

        // The first tick is always due.
        var running = scheduler.Tick(Start, Ct);
        Assert.NotNull(running);
        Assert.False(running.IsCompleted);
        Assert.False(scheduler.WhenIdleAsync().IsCompleted);
        // While one runs, nothing else starts: not before the interval and not after it.
        Assert.Null(scheduler.Tick(Start.AddSeconds(30), Ct));
        Assert.Null(scheduler.Tick(Start.Add(BoardSyncScheduler.Interval), Ct));

        release.SetResult();
        await running;
        await scheduler.WhenIdleAsync();
        Assert.True(scheduler.WhenIdleAsync().IsCompleted);
        Assert.Equal(1, syncs);
        Assert.Equal(1, scopes);

        // Idle but inside the interval measured from the tick that started the sync.
        Assert.Null(scheduler.Tick(Start.AddSeconds(59), Ct));
        var next = scheduler.Tick(Start.Add(BoardSyncScheduler.Interval), Ct);
        Assert.NotNull(next);
        await next;
        await scheduler.WhenIdleAsync();
        Assert.Equal(2, syncs);
        Assert.Equal(2, scopes); // each run resolved the scoped service from its own scope
    }

    [Fact]
    public async Task AFailedSyncNeverFaultsTheTaskTheHostWaitsOnAtShutdown()
    {
        using var provider = Provider(() => throw new InvalidOperationException("viberails.ai is down"), () => { });
        var scheduler = new BoardSyncScheduler(provider.GetRequiredService<IServiceScopeFactory>());

        var running = scheduler.Tick(Start, Ct);
        Assert.NotNull(running);
        await running;
        await scheduler.WhenIdleAsync();
        Assert.True(running.IsCompletedSuccessfully);
    }

    private static ServiceProvider Provider(Func<Task> syncDue, Action resolved)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ =>
        {
            resolved();
            var sync = new Mock<IBoardSyncService>(MockBehavior.Strict);
            sync.Setup(s => s.SyncDueAsync(It.IsAny<CancellationToken>())).Returns(() => syncDue());
            return sync.Object;
        });
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }
}
