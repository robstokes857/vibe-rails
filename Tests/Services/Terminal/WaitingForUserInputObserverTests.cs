using VibeRails.DTOs;
using VibeRails.Services;
using VibeRails.Services.Terminal;
using Xunit;

namespace Tests.Services.Terminal;

public class WaitingForUserInputObserverTests
{
    [Fact]
    public async Task Publishes_WhenSelectionGlyphsArriveAcrossChunks()
    {
        var events = await CaptureEventsAsync(
            "Choose an action:\r\n• Continue\r\n",
            "◦ Cancel\r\n");

        Assert.Single(events);
        Assert.Equal("session_waiting_for_user", events[0].Type);
    }

    [Fact]
    public async Task DoesNotPublish_WhenSpinnerFramesContainWorkingFragmentsNearGlyphs()
    {
        var events = await CaptureEventsAsync(
            "• Wor",
            "king... ◦\r\n");

        Assert.Empty(events);
    }

    [Fact]
    public async Task Publishes_WhenWorkingTextIsNotNearTheSelectionGlyphs()
    {
        var events = await CaptureEventsAsync(
            "Working...\r\n",
            new string('x', 80),
            "\r\nChoose an action:\r\n• Continue\r\n◦ Cancel\r\n");

        Assert.Single(events);
        Assert.Equal("session_waiting_for_user", events[0].Type);
    }

    [Fact]
    public async Task DoesNotPublish_WhenAssistantReplyContainsNestedBulletList()
    {
        var events = await CaptureEventsAsync(
            "Here are the steps:\r\n",
            "• First step\r\n",
            "  ◦ Detail\r\n");

        Assert.Empty(events);
    }

    private static async Task<List<AppEvent>> CaptureEventsAsync(params string[] chunks)
    {
        var eventBus = new AppEventBus();
        var observer = new WaitingForUserInputObserver(eventBus);
        var events = new List<AppEvent>();
        using var subscription = eventBus.Subscribe(events.Add);

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
