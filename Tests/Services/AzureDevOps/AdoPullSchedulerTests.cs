using Microsoft.Extensions.DependencyInjection;
using Moq;
using VibeRails;
using VibeRails.Services.AzureDevOps;
using Xunit;

namespace Tests.Services.AzureDevOps;

public sealed class AdoPullSchedulerTests
{
    private static readonly DateTime Start = new(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// The scheduler is a singleton and the pull service is scoped (it needs the scoped
    /// IBoardService), so resolve the real registration under scope validation, as the host does.
    /// </summary>
    [Fact]
    public async Task RealRegistration_ResolvesTheSchedulerPullServiceAndClientUnderScopeValidation()
    {
        var services = new ServiceCollection();
        MapRegisterServices.Register(services, [], "http://127.0.0.1:12345");
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        Assert.IsType<AdoPullScheduler>(provider.GetRequiredService<IAdoPullScheduler>());
        Assert.IsType<AzureDevOpsClient>(provider.GetRequiredService<IAzureDevOpsClient>());
        Assert.IsType<AdoSecretStore>(provider.GetRequiredService<IAdoSecretStore>());
        // The scoped graph holds a service that only disposes asynchronously.
        await using var scope = provider.CreateAsyncScope();
        Assert.IsType<AdoPullService>(scope.ServiceProvider.GetRequiredService<IAdoPullService>());
        // The 5-second activity drain the scheduler starts between pulls (VIBE-168).
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<AdoDeliveryService>());
        Assert.IsType<AdoSessionSharing>(scope.ServiceProvider.GetRequiredService<IAdoSessionSharing>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<AdoCommentService>());
        Assert.Same(provider.GetRequiredService<AzureDevOpsClient>(), provider.GetRequiredService<IAdoCommentClient>());
    }

    /// <summary>Between pulls a tick starts the activity drain without returning it; a due pull still goes first.</summary>
    [Fact]
    public async Task ADeliveryTickNeverDelaysADuePull()
    {
        var pulls = 0;
        using var provider = Provider(() =>
        {
            Interlocked.Increment(ref pulls);
            return Task.FromResult(true);
        });
        var scheduler = new AdoPullScheduler(provider.GetRequiredService<IServiceScopeFactory>());

        await scheduler.Tick(Start, Ct)!;
        Assert.Null(scheduler.Tick(Start.AddSeconds(1), Ct));
        Assert.Null(scheduler.Tick(Start.Add(AdoPullScheduler.DeliveryInterval).AddSeconds(1), Ct));
        await scheduler.WhenIdleAsync();
        var next = scheduler.Tick(Start.Add(AdoPullScheduler.Interval), Ct);
        Assert.NotNull(next);
        await next;
        await scheduler.WhenIdleAsync();
        Assert.Equal(2, pulls);
    }

    [Fact]
    public async Task Tick_StartsAPullWithoutWaiting_AndNeverOverlapsOrRepeatsWithinTheInterval()
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pulls = 0;
        using var provider = Provider(() =>
        {
            Interlocked.Increment(ref pulls);
            return release.Task;
        });
        var scheduler = new AdoPullScheduler(provider.GetRequiredService<IServiceScopeFactory>());

        var running = scheduler.Tick(Start, Ct);
        Assert.NotNull(running);
        Assert.False(running.IsCompleted);
        Assert.Null(scheduler.Tick(Start.Add(AdoPullScheduler.Interval), Ct));
        Assert.False(scheduler.WhenIdleAsync().IsCompleted);

        release.SetResult(true);
        await running;
        Assert.Null(scheduler.Tick(Start.AddMinutes(5), Ct));
        var next = scheduler.Tick(Start.Add(AdoPullScheduler.Interval), Ct);
        Assert.NotNull(next);
        await next;
        await scheduler.WhenIdleAsync();
        Assert.Equal(2, pulls);
    }

    [Fact]
    public async Task AFailedPullNeverFaultsTheTaskTheHostWaitsOnAtShutdown()
    {
        using var provider = Provider(() => throw new InvalidOperationException("Azure DevOps is down"));
        var scheduler = new AdoPullScheduler(provider.GetRequiredService<IServiceScopeFactory>());

        var running = scheduler.Tick(Start, Ct);
        Assert.NotNull(running);
        await running;
        await scheduler.WhenIdleAsync();
        Assert.True(running.IsCompletedSuccessfully);
    }

    /// <summary>A pull that found the cross-process lock held pulled nothing, so it is due again on the next tick.</summary>
    [Fact]
    public async Task ABusyPullIsDueAgainOnTheNextTick_InsteadOfAfterAWholeInterval()
    {
        var results = new Queue<bool>([false, true]);
        var pulls = 0;
        using var provider = Provider(() =>
        {
            Interlocked.Increment(ref pulls);
            return Task.FromResult(results.Dequeue());
        });
        var scheduler = new AdoPullScheduler(provider.GetRequiredService<IServiceScopeFactory>());

        var busy = scheduler.Tick(Start, Ct);
        Assert.NotNull(busy);
        await busy;
        var retry = scheduler.Tick(Start.AddSeconds(10), Ct);
        Assert.NotNull(retry);
        await retry;
        Assert.Null(scheduler.Tick(Start.AddSeconds(20), Ct));
        Assert.Null(scheduler.Tick(Start.AddMinutes(14), Ct));
        await scheduler.WhenIdleAsync();
        Assert.Equal(2, pulls);
    }

    private static ServiceProvider Provider(Func<Task<bool>> pullDue)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ =>
        {
            var pull = new Mock<IAdoPullService>();
            pull.Setup(p => p.PullDueAsync(It.IsAny<CancellationToken>())).Returns(() => pullDue());
            return pull.Object;
        });
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }
}
