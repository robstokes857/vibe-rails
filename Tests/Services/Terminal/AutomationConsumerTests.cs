using VibeRails.Services.Terminal.Consumers;
using Xunit;

namespace Tests.Services.Terminal;

public sealed class AutomationConsumerTests
{
    [Fact]
    public void SessionWithNoOutput_RequestsShutdownAtTwoMinutes()
    {
        var clock = new ManualTimeProvider();
        using var monitor = new AutomationConsumer(clock);
        monitor.RegisterSession("session-1");

        clock.Advance(AutomationConsumer.IdleTimeout - TimeSpan.FromTicks(1));
        monitor.ScanForIdleSessions();

        Assert.False(monitor.IdleShutdownRequested);
        Assert.False(monitor.IdleShutdownToken.IsCancellationRequested);

        clock.Advance(TimeSpan.FromTicks(1));
        monitor.ScanForIdleSessions();

        Assert.True(monitor.IdleShutdownRequested);
        Assert.True(monitor.IdleShutdownToken.IsCancellationRequested);
        Assert.Equal("session-1", monitor.IdleSessionId);
    }

    [Fact]
    public void EveryNonEmptyRawChunk_RestartsTheFullIdleWindow()
    {
        var clock = new ManualTimeProvider();
        using var monitor = new AutomationConsumer(clock);
        var output = monitor.RegisterSession("session-1");

        clock.Advance(AutomationConsumer.IdleTimeout - TimeSpan.FromSeconds(1));
        output.OnOutput(new byte[] { 0 });

        clock.Advance(AutomationConsumer.IdleTimeout - TimeSpan.FromSeconds(1));
        monitor.ScanForIdleSessions();
        Assert.False(monitor.IdleShutdownRequested);

        clock.Advance(TimeSpan.FromSeconds(1));
        monitor.ScanForIdleSessions();
        Assert.True(monitor.IdleShutdownRequested);
    }

    [Fact]
    public void EmptyChunk_DoesNotCountAsPtyActivity()
    {
        var clock = new ManualTimeProvider();
        using var monitor = new AutomationConsumer(clock);
        var output = monitor.RegisterSession("session-1");

        clock.Advance(AutomationConsumer.IdleTimeout - TimeSpan.FromSeconds(1));
        output.OnOutput(ReadOnlyMemory<byte>.Empty);
        clock.Advance(TimeSpan.FromSeconds(1));
        monitor.ScanForIdleSessions();

        Assert.True(monitor.IdleShutdownRequested);
    }

    [Fact]
    public void ClosedSession_IsRemovedAndCannotRequestShutdown()
    {
        var clock = new ManualTimeProvider();
        using var monitor = new AutomationConsumer(clock);
        var output = monitor.RegisterSession("session-1");

        output.OnClosed();
        output.OnClosed();
        clock.Advance(AutomationConsumer.IdleTimeout);
        monitor.ScanForIdleSessions();

        Assert.Equal(0, monitor.ActiveSessionCount);
        Assert.False(monitor.IdleShutdownRequested);
    }

    [Fact]
    public void OneMonitorTracksMultipleSessionsIndependently()
    {
        var clock = new ManualTimeProvider();
        using var monitor = new AutomationConsumer(clock);
        var activeOutput = monitor.RegisterSession("active-session");
        monitor.RegisterSession("idle-session");

        clock.Advance(TimeSpan.FromSeconds(90));
        activeOutput.OnOutput(new byte[] { 1 });
        clock.Advance(TimeSpan.FromSeconds(30));
        monitor.ScanForIdleSessions();

        Assert.True(monitor.TimerStarted);
        Assert.Equal(2, monitor.ActiveSessionCount);
        Assert.Equal("idle-session", monitor.IdleSessionId);
    }

    [Fact]
    public void RepeatedScans_RequestShutdownOnlyOnce()
    {
        var clock = new ManualTimeProvider();
        using var monitor = new AutomationConsumer(clock);
        monitor.RegisterSession("session-1");
        var callbackCount = 0;
        using var registration = monitor.IdleShutdownToken.Register(() => callbackCount++);

        clock.Advance(AutomationConsumer.IdleTimeout);
        monitor.ScanForIdleSessions();
        monitor.ScanForIdleSessions();
        monitor.ScanForIdleSessions();

        Assert.Equal(1, callbackCount);
    }

    [Fact]
    public void TimerStartsLazilyAndIsSharedByRegistrations()
    {
        var clock = new ManualTimeProvider();
        using var monitor = new AutomationConsumer(clock);

        Assert.False(monitor.TimerStarted);
        monitor.RegisterSession("session-1");
        Assert.True(monitor.TimerStarted);
        monitor.RegisterSession("session-2");
        Assert.True(monitor.TimerStarted);
    }
}
