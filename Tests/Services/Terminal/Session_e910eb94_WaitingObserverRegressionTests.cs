using System.Buffers.Binary;
using System.Text;
using VibeRails.DTOs;
using VibeRails.Services;
using VibeRails.Services.Terminal;
using Xunit;

namespace Tests.Services.Terminal;

/// <summary>
/// Regression tests for codex session e910eb94-ef52-4d6e-af33-36284851eb3a.
///
/// Rob's report: tab status flipped to "Waiting for user input" ~14s after he
/// submitted a message, while the visible screen was still the Working
/// spinner. Codex did legitimately reach a permission prompt later — that
/// second fire is the true positive captured by the composer fixture below.
///
/// The false-positive chunk shape is *different* from Session_881cd29d's per-
/// char letter rewrites: codex emits a stream of ~124-byte CUP+EL cursor-park
/// frames at ~30Hz during certain Working sub-phases, and they're bit-
/// identical to each other (cursor lands at the same spot each frame because
/// the row-content updates happen in adjacent &lt;50-byte chunks the observer
/// ignores). The unique-big-chunk discriminator therefore sees "one big chunk
/// repeated many times" — the canonical idle signature — and fires.
///
/// Working_NoFireDuringCupParkAnimation: ~14s window straddling fire #1 at
///   T+14s. Must produce zero events.
///
/// ComposerWaiting_FiresExactlyOneEvent: ~18s window ending right before the
///   user typed the next message. Codex's response output had stopped and the
///   composer cursor was blinking — a genuine "waiting" state. Must produce
///   exactly one event.
/// </summary>
public sealed class Session_e910eb94_WaitingObserverRegressionTests
{
    private const string SessionId = "e910eb94-ef52-4d6e-af33-36284851eb3a";

    private static readonly string WorkingFixturePath = Path.Combine(
        AppContext.BaseDirectory,
        "Services",
        "Terminal",
        "Fixtures",
        "session_e910eb94_codex_working_falsefire.fixture.bin");

    private static readonly string ComposerFixturePath = Path.Combine(
        AppContext.BaseDirectory,
        "Services",
        "Terminal",
        "Fixtures",
        "session_e910eb94_codex_composer_truefire.fixture.bin");

    private readonly Xunit.ITestOutputHelper _output;

    public Session_e910eb94_WaitingObserverRegressionTests(Xunit.ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task Working_NoFireDuringCupParkAnimation()
    {
        var events = await ReplayFixtureAsync(WorkingFixturePath);
        foreach (var ev in events)
            _output.WriteLine($"unexpected event: {ev.Type}");
        Assert.Empty(events);
    }

    [Fact]
    public async Task ComposerWaiting_FiresExactlyOneEvent()
    {
        var events = await ReplayFixtureAsync(ComposerFixturePath);
        foreach (var ev in events)
            _output.WriteLine($"event: {ev.Type}");
        Assert.Single(events);
        Assert.Equal("session_waiting_for_user", events[0].Type);
    }

    private async Task<List<AppEvent>> ReplayFixtureAsync(string path)
    {
        var chunks = LoadFixture(path);
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 5, 14, 12, 22, 0, TimeSpan.Zero));
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
            TimestampUtc: clock.GetUtcNow()));

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
                TimestampUtc: clock.GetUtcNow()));
            if (events.Count != lastEventCount)
            {
                _output.WriteLine($"event #{events.Count} fired at T+{chunk.MsOffset / 1000.0:F2}s");
                lastEventCount = events.Count;
            }
        }
        return events;
    }

    private static List<TimedChunk> LoadFixture(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var pos = 0;
        var count = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(pos, 4));
        pos += 4;
        var chunks = new List<TimedChunk>((int)count);
        for (var i = 0u; i < count; i++)
        {
            var byteCount = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(pos, 4));
            pos += 4;
            var msOffset = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(pos, 4));
            pos += 4;
            var data = new byte[byteCount];
            Array.Copy(bytes, pos, data, 0, byteCount);
            pos += (int)byteCount;
            chunks.Add(new TimedChunk(data, msOffset));
        }
        return chunks;
    }

    private readonly record struct TimedChunk(byte[] Bytes, uint MsOffset);

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow;

        public ManualTimeProvider(DateTimeOffset start)
        {
            _utcNow = start;
        }

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void SetUtcNow(DateTimeOffset value) => _utcNow = value;
    }
}
