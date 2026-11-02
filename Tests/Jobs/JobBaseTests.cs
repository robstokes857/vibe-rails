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
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

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

    [Fact]
    public async Task SustainedCpuPressureDefersAtMostTheCapThenTheJobKeepsItsCadence()
    {
        var clock = new ManualClock();
        var pressure = new Pressure { Cpu = true };
        var job = new BackloggedJob(pressure.Resources(), new CountingLogger(), clock);

        // Deferred ticks wait the full interval until the episode has lasted the cap.
        var delays = new List<TimeSpan>();
        while (job.Ticks == 0) { var delay = await job.RunTickAsync(Ct); delays.Add(delay); clock.Now += delay; }
        Assert.All(delays.SkipLast(1), d => Assert.Equal(BackloggedJob.IntervalValue, d));
        Assert.Equal(BackloggedJob.CpuCap, clock.Now - ManualClock.Start - BackloggedJob.FollowUpValue);

        // The episode continues: every tick runs at the follow-up cadence instead of one per cap.
        for (var i = 0; i < 20; i++) Assert.Equal(BackloggedJob.FollowUpValue, await job.RunTickAsync(Ct));
        Assert.Equal(21, job.Ticks);

        // A quiet tick ends the episode; the next busy spell defers again.
        pressure.Cpu = false;
        Assert.Equal(BackloggedJob.FollowUpValue, await job.RunTickAsync(Ct));
        pressure.Cpu = true;
        Assert.Equal(BackloggedJob.IntervalValue, await job.RunTickAsync(Ct));
        Assert.Equal(22, job.Ticks);
    }

    [Fact]
    public async Task MemoryPressureDefersWithoutACap()
    {
        var clock = new ManualClock();
        var job = new BackloggedJob(new Pressure { Memory = true }.Resources(), new CountingLogger(), clock);

        for (var i = 0; i < 100; i++) clock.Now += await job.RunTickAsync(Ct);

        Assert.True(clock.Now - ManualClock.Start > TimeSpan.FromHours(8));
        Assert.Equal(0, job.Ticks);
    }

    [Fact]
    public async Task AJobWithItsOwnCpuPolicyIsNotDeferredForCpu()
    {
        var clock = new ManualClock();
        var job = new BackloggedJob(new Pressure { Cpu = true }.Resources(), new CountingLogger(), clock, defersForCpu: false);

        Assert.Equal(BackloggedJob.FollowUpValue, await job.RunTickAsync(Ct));
        Assert.Equal(1, job.Ticks);
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
        resources.SetupGet(r => r.IsCpuUnderPressure).Returns(underPressure);
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

    /// <summary>A job that always has a backlog, driven tick by tick on a manual clock.</summary>
    private sealed class BackloggedJob(ISystemResourceService resources, ILogger logger, TimeProvider clock, bool defersForCpu = true)
        : JobBase(logger, resources, clock)
    {
        public static readonly TimeSpan IntervalValue = TimeSpan.FromMinutes(5);
        public static readonly TimeSpan FollowUpValue = TimeSpan.FromSeconds(5);
        public static readonly TimeSpan CpuCap = TimeSpan.FromMinutes(30);
        public int Ticks;
        protected override TimeSpan Interval => IntervalValue;
        protected override TimeSpan NextDelay => FollowUpValue;
        protected override JobPriority Priority => JobPriority.Med;
        protected override TimeSpan MaxCpuDeferral => CpuCap;
        protected override bool DefersForCpuPressure => defersForCpu;
        protected override Task ExecuteJob(CancellationToken cancellationToken)
        {
            Ticks++;
            return Task.CompletedTask;
        }
    }

    private sealed class Pressure
    {
        public bool Cpu;
        public bool Memory;

        public ISystemResourceService Resources()
        {
            var resources = new Mock<ISystemResourceService>();
            resources.SetupGet(r => r.IsCpuUnderPressure).Returns(() => Cpu);
            resources.SetupGet(r => r.IsMemoryUnderPressure).Returns(() => Memory);
            resources.SetupGet(r => r.Current).Returns(new ResourceSnapshot { SystemCpuPercent = 95 });
            return resources.Object;
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        public static readonly DateTimeOffset Start = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
        public DateTimeOffset Now = Start;
        public override DateTimeOffset GetUtcNow() => Now;
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
