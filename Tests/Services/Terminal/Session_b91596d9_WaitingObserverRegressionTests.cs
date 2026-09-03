using System.Text;
using VibeRails.DTOs;
using VibeRails.Services;
using VibeRails.Services.Terminal;
using Xunit;

namespace Tests.Services.Terminal;

/// <summary>
/// Regression test for Codex session b91596d9-eace-4df6-9a05-d9ddd531163e.
/// The tab flipped to "Waiting for user input" 56 seconds into a live code
/// review while Codex still visibly rendered "Working (... esc to interrupt)".
/// Current Codex builds fragment that row into hundreds of tiny synchronized-
/// output patches; four incidental repeated large chunks satisfied the older
/// chunk-repetition heuristic even though the agent was still active.
///
/// Fixture is gitignored (real session bytes); the test Skip()s when absent
/// so CI and contributors without the fixture pass cleanly.
/// </summary>
public sealed class Session_b91596d9_WaitingObserverRegressionTests
{
    private const string SessionId = "b91596d9-eace-4df6-9a05-d9ddd531163e";

    private static readonly string FixturePath = Path.Combine(
        AppContext.BaseDirectory,
        "Services",
        "Terminal",
        "Fixtures",
        "session_b91596d9_codex_working_falsefire.fixture.bin");

    [Fact]
    public async Task WorkingScreen_DoesNotPublishWaitingEvent()
    {
        if (!File.Exists(FixturePath))
            Assert.Skip($"Fixture not present: {FixturePath}. Run python-scripts/export_chunks_fixture.py against the session locally to regenerate.");

        var chunks = TerminalTestFixtures.LoadFixture(FixturePath);
        Assert.NotEmpty(chunks);

        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 26, 22, 18, 17, TimeSpan.Zero));
        var bus = new AppEventBus();
        var observer = new WaitingForUserInputObserver(bus, clock);
        var events = new List<AppEvent>();
        bus.Subscribe(events.Add);

        await observer.OnSessionStartAsync(new TerminalSessionStartEvent(
            SessionId: SessionId,
            Cli: "codex",
            WorkDir: string.Empty,
            EnvName: null,
            SetupCommands: Array.Empty<string>(),
            LaunchCommand: string.Empty,
            TimestampUtc: clock.GetUtcNow()), TestContext.Current.CancellationToken);

        var startUtc = clock.GetUtcNow();
        foreach (var chunk in chunks)
        {
            clock.SetUtcNow(startUtc.AddMilliseconds(chunk.MsOffset));
            await observer.OnTerminalIoAsync(new TerminalIoEvent(
                SessionId: SessionId,
                Direction: TerminalIoDirection.Output,
                Source: TerminalIoSource.Pty,
                Text: Encoding.UTF8.GetString(chunk.Bytes),
                TimestampUtc: clock.GetUtcNow()), TestContext.Current.CancellationToken);
        }

        Assert.Empty(events);
    }
}
