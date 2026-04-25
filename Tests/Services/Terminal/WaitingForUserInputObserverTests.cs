using VibeRails.DTOs;
using VibeRails.Services;
using VibeRails.Services.Terminal;
using Xunit;

namespace Tests.Services.Terminal;

public class WaitingForUserInputObserverTests
{
    [Fact]
    public async Task Publishes_WhenBothBulletGlyphsPresent()
    {
        var events = await CaptureEventsAsync("codex", "Choose an action:\r\n• Continue\r\n◦ Cancel\r\n");

        Assert.Single(events);
        Assert.Equal("session_waiting_for_user", events[0].Type);
    }

    [Fact]
    public async Task DoesNotPublish_WhenBufferContainsWorkingFragment()
    {
        var events = await CaptureEventsAsync("codex", "• Working... ◦\r\n");

        Assert.Empty(events);
    }

    [Fact]
    public async Task DoesNotPublish_WhenCodexTimerRedrawFollowsRecentWorkingSignal()
    {
        var clock = new ManualTimeProvider();
        var (observer, events) = await CreateObserverAsync("codex", clock);

        await SendOutputAsync(observer, "session-1", "Working (0s • esc to interrupt) ◦\r\n");

        clock.Advance(TimeSpan.FromSeconds(16));
        await SendOutputAsync(observer, "session-1", "• ◦ 16s\r\n");

        Assert.Empty(events);
    }

    [Fact]
    public async Task Publishes_WhenRealPromptFollowsRecentWorkingSignal()
    {
        var clock = new ManualTimeProvider();
        var (observer, events) = await CreateObserverAsync("codex", clock);

        await SendOutputAsync(observer, "session-1", "Working (0s • esc to interrupt) ◦\r\n");

        clock.Advance(TimeSpan.FromSeconds(16));
        await SendOutputAsync(observer, "session-1", "Choose an action:\r\n• Continue\r\n◦ Cancel\r\n");

        Assert.Single(events);
        Assert.Equal("session_waiting_for_user", events[0].Type);
    }

    [Fact]
    public async Task DoesNotPublish_WhenOnlyOneGlyphPresent()
    {
        var events = await CaptureEventsAsync("codex", "• Only this\r\n");

        Assert.Empty(events);
    }

    [Fact]
    public async Task DoesNotPublish_WhenSessionIsNotCodex()
    {
        var events = await CaptureEventsAsync("claude", "Choose an action:\r\n• Continue\r\n◦ Cancel\r\n");

        Assert.Empty(events);
    }

    private static async Task<List<AppEvent>> CaptureEventsAsync(string cli, params string[] chunks)
    {
        var (observer, events) = await CreateObserverAsync(cli, TimeProvider.System);

        foreach (var chunk in chunks)
        {
            await SendOutputAsync(observer, "session-1", chunk);
        }

        return events;
    }

    private static async Task<(WaitingForUserInputObserver Observer, List<AppEvent> Events)> CreateObserverAsync(
        string cli,
        TimeProvider timeProvider)
    {
        var eventBus = new AppEventBus();
        var observer = new WaitingForUserInputObserver(eventBus, timeProvider);
        var events = new List<AppEvent>();
        eventBus.Subscribe(events.Add);

        await observer.OnSessionStartAsync(new TerminalSessionStartEvent(
            SessionId: "session-1",
            Cli: cli,
            WorkDir: string.Empty,
            EnvName: null,
            SetupCommands: Array.Empty<string>(),
            LaunchCommand: string.Empty,
            TimestampUtc: DateTimeOffset.UtcNow));

        return (observer, events);
    }

    private static async Task SendOutputAsync(WaitingForUserInputObserver observer, string sessionId, string chunk)
    {
        await observer.OnTerminalIoAsync(new TerminalIoEvent(
            SessionId: sessionId,
            Direction: TerminalIoDirection.Output,
            Source: TerminalIoSource.Pty,
            Text: chunk,
            TimestampUtc: DateTimeOffset.UtcNow));
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow = new(2026, 4, 25, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan value)
        {
            _utcNow += value;
        }
    }
}
