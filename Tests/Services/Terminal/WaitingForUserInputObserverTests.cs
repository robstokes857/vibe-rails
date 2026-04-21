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
        var eventBus = new AppEventBus();
        var observer = new WaitingForUserInputObserver(eventBus);
        var events = new List<AppEvent>();
        using var subscription = eventBus.Subscribe(events.Add);

        await observer.OnSessionStartAsync(new TerminalSessionStartEvent(
            SessionId: "session-1",
            Cli: cli,
            WorkDir: string.Empty,
            EnvName: null,
            SetupCommands: Array.Empty<string>(),
            LaunchCommand: string.Empty,
            TimestampUtc: DateTimeOffset.UtcNow));

        foreach (var chunk in chunks)
        {
            await observer.OnTerminalIoAsync(new TerminalIoEvent(
                SessionId: "session-1",
                Direction: TerminalIoDirection.Output,
                Source: TerminalIoSource.Pty,
                Text: chunk,
                TimestampUtc: DateTimeOffset.UtcNow));
        }

        return events;
    }
}
