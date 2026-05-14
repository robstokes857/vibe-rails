using System.Text;
using VibeRails.DTOs;
using VibeRails.Services;
using VibeRails.Services.Terminal;
using Xunit;

namespace Tests.Services.Terminal;

/// <summary>
/// Regression test for codex session 0ebd404d-fb64-452c-85f0-e345bf799457.
/// This is a short (~102 second) session that ends with codex idle at a
/// waiting prompt for ~42 seconds.  Replaying it through the observer with
/// the original PTY chunk timing must produce exactly one
/// session_waiting_for_user event, fired some time after codex transitions
/// from "Working" to the idle-spinner state at ~T+60s.
/// </summary>
public sealed class Session_0ebd404d_WaitingObserverRegressionTests
{
    private static readonly string FixturePath = Path.Combine(
        AppContext.BaseDirectory,
        "Services",
        "Terminal",
        "Fixtures",
        "session_0ebd404d_codex_idle_at_end.fixture.bin");

    private readonly Xunit.ITestOutputHelper _output;

    public Session_0ebd404d_WaitingObserverRegressionTests(Xunit.ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task Replay_FiresExactlyOneWaitingEventAfterCodexGoesIdle()
    {
        var chunks = TerminalTestFixtures.LoadFixture(FixturePath);
        Assert.NotEmpty(chunks);

        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 4, 26, 4, 27, 29, TimeSpan.Zero));
        var bus = new AppEventBus();
        var observer = new WaitingForUserInputObserver(bus, clock);
        var events = new List<AppEvent>();
        bus.Subscribe(events.Add);

        const string sessionId = "0ebd404d-fb64-452c-85f0-e345bf799457";
        await observer.OnSessionStartAsync(new TerminalSessionStartEvent(
            SessionId: sessionId,
            Cli: "codex",
            WorkDir: string.Empty,
            EnvName: null,
            SetupCommands: Array.Empty<string>(),
            LaunchCommand: string.Empty,
            TimestampUtc: clock.GetUtcNow()));

        var startUtc = clock.GetUtcNow();
        var lastEventCount = 0;
        foreach (var chunk in chunks)
        {
            clock.SetUtcNow(startUtc.AddMilliseconds(chunk.MsOffset));
            await observer.OnTerminalIoAsync(new TerminalIoEvent(
                SessionId: sessionId,
                Direction: TerminalIoDirection.Output,
                Source: TerminalIoSource.Pty,
                Text: Encoding.UTF8.GetString(chunk.Bytes),
                TimestampUtc: clock.GetUtcNow()));
            if (events.Count != lastEventCount)
            {
                _output.WriteLine($"event #{events.Count} fired at T+{chunk.MsOffset / 1000.0:F2}s");
                lastEventCount = events.Count;
            }
        }

        Assert.Single(events);
        Assert.Equal("session_waiting_for_user", events[0].Type);
    }
}
