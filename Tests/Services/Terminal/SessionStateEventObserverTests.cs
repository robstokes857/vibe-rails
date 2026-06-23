using VibeRails.DTOs;
using VibeRails.Services;
using VibeRails.Services.Terminal;
using Xunit;

namespace Tests.Services.Terminal;

public sealed class SessionStateEventObserverTests
{
    [Fact]
    public async Task InputSubmit_PublishesSanitizedSessionInputEvent()
    {
        var bus = new AppEventBus();
        var events = new List<AppEvent>();
        using var sub = bus.Subscribe(events.Add);
        var observer = new SessionStateEventObserver(bus);

        await observer.OnTerminalIoAsync(new TerminalIoEvent(
            "session-1",
            TerminalIoDirection.Input,
            TerminalIoSource.RemoteWebUi,
            "\r",
            DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);

        var appEvent = Assert.Single(events);
        Assert.Equal("session_input", appEvent.Type);
        Assert.Equal("session-1", appEvent.Payload.GetProperty("sessionId").GetString());
        Assert.Equal("submit", appEvent.Payload.GetProperty("kind").GetString());
        Assert.Equal("RemoteWebUi", appEvent.Payload.GetProperty("source").GetString());
        Assert.DoesNotContain("text", appEvent.Payload.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InputPrintable_DoesNotPublishPerKeystrokeEvent()
    {
        var bus = new AppEventBus();
        var events = new List<AppEvent>();
        using var sub = bus.Subscribe(events.Add);
        var observer = new SessionStateEventObserver(bus);

        await observer.OnTerminalIoAsync(new TerminalIoEvent(
            "session-1",
            TerminalIoDirection.Input,
            TerminalIoSource.LocalWebUi,
            "z",
            DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);

        Assert.Empty(events);
    }

    [Theory]
    [InlineData("\x1b[B")]
    [InlineData("\x1b[24;80R")]
    [InlineData("\x1b[200~pasted\x1b[201~")]
    public async Task InputControlSequences_DoNotPublishSessionInputEvent(string input)
    {
        var bus = new AppEventBus();
        var events = new List<AppEvent>();
        using var sub = bus.Subscribe(events.Add);
        var observer = new SessionStateEventObserver(bus);

        await observer.OnTerminalIoAsync(new TerminalIoEvent(
            "session-1",
            TerminalIoDirection.Input,
            TerminalIoSource.LocalWebUi,
            input,
            DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);

        Assert.Empty(events);
    }
}
