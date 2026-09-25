using Microsoft.Extensions.DependencyInjection;
using Moq;
using VibeRails;
using VibeRails.Services.Jira;
using Xunit;

namespace Tests.Services.Jira;

public sealed class JiraPullSchedulerTests
{
    private static readonly DateTime Start = new(2026, 9, 25, 12, 0, 0, DateTimeKind.Utc);

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// The scheduler is a singleton and the pull service is scoped (it needs the scoped
    /// IBoardService). Taking the pull service in the constructor made the host's scope
    /// validation refuse to start the app, so resolve the real registration the same way.
    /// </summary>
    [Fact]
    public void RealRegistration_ResolvesTheSingletonSchedulerUnderScopeValidation()
    {
        var services = new ServiceCollection();
        MapRegisterServices.Register(services, [], "http://127.0.0.1:12345");
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        Assert.IsType<JiraPullScheduler>(provider.GetRequiredService<IJiraPullScheduler>());
    }

    /// <summary>
    /// A pull can take minutes and must not hold up the Automation cycle that ticks it, or run
    /// twice at once, or repeat inside the interval.
    /// </summary>
    [Fact]
    public async Task Tick_StartsAPullWithoutWaiting_AndNeverOverlapsOrRepeatsWithinTheInterval()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pulls = 0;
        using var provider = Provider(() =>
        {
            Interlocked.Increment(ref pulls);
            return release.Task;
        });
        var scheduler = new JiraPullScheduler(provider.GetRequiredService<IServiceScopeFactory>());

        var running = scheduler.Tick(Start, Ct);
        Assert.NotNull(running);
        Assert.False(running.IsCompleted);
        Assert.Null(scheduler.Tick(Start.Add(JiraPullScheduler.Interval), Ct));

        release.SetResult();
        await running;
        Assert.Null(scheduler.Tick(Start.AddMinutes(5), Ct));
        var next = scheduler.Tick(Start.Add(JiraPullScheduler.Interval), Ct);
        Assert.NotNull(next);
        await next;
        await scheduler.WhenIdleAsync();
        Assert.Equal(2, pulls);
    }

    [Fact]
    public async Task AFailedPullNeverFaultsTheTaskTheHostWaitsOnAtShutdown()
    {
        using var provider = Provider(() => throw new InvalidOperationException("Jira is down"));
        var scheduler = new JiraPullScheduler(provider.GetRequiredService<IServiceScopeFactory>());

        var running = scheduler.Tick(Start, Ct);
        Assert.NotNull(running);
        await running;
        await scheduler.WhenIdleAsync();
        Assert.True(running.IsCompletedSuccessfully);
    }

    private static ServiceProvider Provider(Func<Task> pullDue)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ =>
        {
            var pull = new Mock<IJiraPullService>();
            pull.Setup(p => p.PullDueAsync(It.IsAny<CancellationToken>())).Returns(() => pullDue());
            return pull.Object;
        });
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }
}
