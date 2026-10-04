using VibeRails.Services.Terminal;
using Xunit;

namespace Tests.Services.Terminal;

public sealed class AgentSessionEndSchedulerTests
{
    [Fact]
    public void GracePeriodIsThirtySecondsAndRetriesKeepTheFirstDeadline()
    {
        var clock = new TimerClock();
        var stopped = new List<string>();
        using var scheduler = new AgentSessionEndScheduler(id => id == "own", id => { stopped.Add(id); return Task.FromResult(true); }, clock);
        Assert.Null(scheduler.Schedule("other"));
        var deadline = scheduler.Schedule("own");
        Assert.Equal(clock.GetUtcNow().AddSeconds(30), deadline);
        clock.Advance(29);
        Assert.Empty(stopped);
        Assert.Equal(deadline, scheduler.Schedule("own"));
        clock.Advance(1);
        Assert.Equal(["own"], stopped);
        clock.Advance(60);
        Assert.Single(stopped);
    }

    [Fact]
    public void ReplacedSessionsAndDisposedHostsCannotBeStoppedByOldTimers()
    {
        var clock = new TimerClock();
        var stopped = new List<string>();
        var current = "first";
        using var scheduler = new AgentSessionEndScheduler(id => id == current, id => { if (id == current) stopped.Add(id); return Task.FromResult(id == current); }, clock);
        scheduler.Schedule("first"); current = "second";
        clock.Advance(30);
        Assert.Empty(stopped);
        scheduler.Schedule("second"); scheduler.Dispose();
        clock.Advance(30);
        Assert.Empty(stopped);
    }

    internal sealed class TimerClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.Parse("2026-10-03T00:00:00Z");
        private readonly List<Timer> timers = [];
        public override DateTimeOffset GetUtcNow() => now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new Timer(now + dueTime, () => callback(state)); timers.Add(timer); return timer;
        }
        public void Advance(int seconds)
        {
            now = now.AddSeconds(seconds);
            foreach (var timer in timers.ToArray()) if (!timer.Disposed && timer.Due <= now) { timer.Dispose(); timer.Fire(); }
        }
        private sealed class Timer(DateTimeOffset due, Action fire) : ITimer
        {
            public DateTimeOffset Due => due;
            public bool Disposed { get; private set; }
            public void Fire() => fire();
            public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
            public void Dispose() => Disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
