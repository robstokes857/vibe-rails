using System.Text;
using VibeRails.DTOs;
using VibeRails.Services;
using VibeRails.Services.Terminal;
using Xunit;

namespace Tests.Services.Terminal;

/// <summary>
/// Regression test for codex session 8522be5b-3376-4b02-baba-b7169345a092.
///
/// Bug: codex was idle at a "waiting for user input" prompt for ~minutes,
/// but WaitingForUserInputObserver never fired session_waiting_for_user.
///
/// Cause (confirmed against the byte stream): codex's idle keepalive cadence
/// produces only ONE chunk ≥50 bytes per second — a per-second pattern of
///     OSC ]0;[ ! ] Action Required\a    (39 bytes, below threshold)
///     ESC[?2026h                         (8 bytes,  below threshold)
///     ESC[?2026l                         (8 bytes,  below threshold)
///     CUP/EL row-clear sweep             (~149 bytes, ABOVE threshold)
/// Inside the observer's 5-second window that is at most 5 big chunks. The
/// gates were tuned for a denser cadence: IdleMinBigChunkSamples=40 and
/// IdleMinTopChunkCount=20 cannot be met, so Classify never returns Idle.
///
/// Fixture is gitignored (real session bytes); the test Skip()s when absent
/// so CI and contributors without the fixture pass cleanly.
/// </summary>
public sealed class Session_8522be5b_WaitingObserverRegressionTests
{
    private const string SessionId = "8522be5b-3376-4b02-baba-b7169345a092";

    private static readonly string FixturePath = Path.Combine(
        AppContext.BaseDirectory,
        "Services",
        "Terminal",
        "Fixtures",
        "session_8522be5b_codex_idle_at_end.fixture.bin");

    private readonly Xunit.ITestOutputHelper _output;

    public Session_8522be5b_WaitingObserverRegressionTests(Xunit.ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task Replay_FiresExactlyOneWaitingEventAfterCodexGoesIdle()
    {
        if (!File.Exists(FixturePath))
            Assert.Skip($"Fixture not present: {FixturePath}. Run python-scripts/export_chunks_fixture.py against the session locally to regenerate.");

        var chunks = TerminalTestFixtures.LoadFixture(FixturePath);
        Assert.NotEmpty(chunks);

        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 5, 6, 21, 29, 50, TimeSpan.Zero));
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
        var lastEventCount = 0;
        foreach (var chunk in chunks)
        {
            clock.SetUtcNow(startUtc.AddMilliseconds(chunk.MsOffset));
            await observer.OnTerminalIoAsync(new TerminalIoEvent(
                SessionId: SessionId,
                Direction: TerminalIoDirection.Output,
                Source: TerminalIoSource.Pty,
                Text: Encoding.UTF8.GetString(chunk.Bytes),
                TimestampUtc: clock.GetUtcNow()), TestContext.Current.CancellationToken);
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
