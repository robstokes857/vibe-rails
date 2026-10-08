using Microsoft.Extensions.Logging;
using Moq;
using VibeRails.Jobs;
using VibeRails.Services;
using Xunit;

namespace Tests.Jobs;

/// <summary>The adaptive tick delay added for VB-2GUR8-187: follow up while a job has a backlog, otherwise keep the interval.</summary>
public sealed class JobBaseTests
{
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(500);

    [Fact]
    public async Task ABackloggedJobFollowsUpQuicklyAndReturnsToItsIntervalOnceIdle()
    {
        var job = new AdaptiveJob(Resources(underPressure: false), new CountingLogger());
        await job.StartAsync(CancellationToken.None);
        try
        {
            // The ten-minute interval cannot tick inside this test; only the follow-up delay can produce these.
            await WaitUntil(() => Volatile.Read(ref job.Ticks) >= 3);
            await Task.Delay(200);
            var settled = Volatile.Read(ref job.Ticks);
            Assert.InRange(settled, 3, 4);
            await Task.Delay(Settle);
            Assert.Equal(settled, Volatile.Read(ref job.Ticks));
        }
        finally { await job.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task APressureDeferralWaitsForTheIntervalInsteadOfTheFollowUpDelay()
    {
        var logger = new CountingLogger();
        var job = new AdaptiveJob(Resources(underPressure: true), logger);
        await job.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntil(() => Volatile.Read(ref logger.Deferrals) >= 1);
            await Task.Delay(200);
            var settled = Volatile.Read(ref logger.Deferrals);
            Assert.InRange(settled, 1, 2);
            await Task.Delay(Settle);
            Assert.Equal(settled, Volatile.Read(ref logger.Deferrals));
            Assert.Equal(0, job.Ticks);
        }
        finally { await job.StopAsync(CancellationToken.None); }
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "the job never ticked");
            await Task.Delay(10);
        }
    }

    private static ISystemResourceService Resources(bool underPressure)
    {
        var resources = new Mock<ISystemResourceService>();
        resources.SetupGet(r => r.IsUnderPressure).Returns(underPressure);
        resources.SetupGet(r => r.Current).Returns(new ResourceSnapshot { ProcessCpuPercent = underPressure ? 95 : 5 });
        return resources.Object;
    }

    private sealed class AdaptiveJob(ISystemResourceService resources, ILogger logger) : JobBase(logger, resources)
    {
        public int Ticks;
        private volatile bool _backlog = true;
        protected override TimeSpan Interval => TimeSpan.FromMinutes(10);
        protected override TimeSpan NextDelay => _backlog ? TimeSpan.FromMilliseconds(20) : Interval;
        protected override JobPriority Priority => JobPriority.Med;
        protected override Task ExecuteJob(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref Ticks) >= 3) _backlog = false;
            return Task.CompletedTask;
        }
    }

    private sealed class CountingLogger : ILogger
    {
        public int Deferrals;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (formatter(state, exception).Contains("Deferring", StringComparison.Ordinal)) Interlocked.Increment(ref Deferrals);
        }
    }
}
