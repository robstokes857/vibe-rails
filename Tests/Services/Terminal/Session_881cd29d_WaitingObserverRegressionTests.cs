using System.Buffers.Binary;
using System.Text;
using VibeRails.DTOs;
using VibeRails.Services;
using VibeRails.Services.Terminal;
using Xunit;

namespace Tests.Services.Terminal;

/// <summary>
/// Regression tests for codex session 881cd29d-4689-424b-952d-9d7355d5ce30
/// (the "Waiting for user input" false-positive bug Rob reported).
///
/// The bug: codex animates the "Working (Xs • esc to interrupt)" line by
/// patching individual characters via cursor-positioning ANSI sequences inside
/// synchronized-output mode. Each chunk is tiny (~80–150 bytes) and contains
/// no literal "Working" or timer substring after ANSI stripping. The original
/// observer's byte-bounded buffer was cleared on every 15s check, so it
/// repeatedly saw glyph pairs `•◦` with no working signal in scope and
/// fired a false `session_waiting_for_user` event.
///
/// PerCharSpinner_NoWaitingFires_DuringCodexWorkingAnimation: ~30s window
///   captured during the per-char animation. No event must fire — codex is
///   actively working the whole time.
///
/// ApprovalMenu_FiresExactlyOneEventWhenCodexAsksUser: ~35s window captured
///   around the codex "Press enter to confirm or esc to cancel" approval menu.
///   Exactly one event must fire when the menu appears.
/// </summary>
public sealed class Session_881cd29d_WaitingObserverRegressionTests
{
    private const string SessionId = "881cd29d-4689-424b-952d-9d7355d5ce30";

    private static readonly string PerCharFixturePath = Path.Combine(
        AppContext.BaseDirectory,
        "Services",
        "Terminal",
        "Fixtures",
        "session_881cd29d_codex_perchar_spinner.fixture.bin");

    private static readonly string MenuFixturePath = Path.Combine(
        AppContext.BaseDirectory,
        "Services",
        "Terminal",
        "Fixtures",
        "session_881cd29d_codex_approval_menu.fixture.bin");

    private readonly Xunit.ITestOutputHelper _output;

    public Session_881cd29d_WaitingObserverRegressionTests(Xunit.ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task PerCharSpinner_NoWaitingFires_DuringCodexWorkingAnimation()
    {
        var events = await ReplayFixtureAsync(PerCharFixturePath);
        foreach (var ev in events)
            _output.WriteLine($"unexpected event: {ev.Type}");
        Assert.Empty(events);
    }

    [Fact]
    public async Task ApprovalMenu_FiresExactlyOneEventWhenCodexAsksUser()
    {
        var events = await ReplayFixtureAsync(MenuFixturePath);
        foreach (var ev in events)
            _output.WriteLine($"event: {ev.Type}");
        Assert.Single(events);
        Assert.Equal("session_waiting_for_user", events[0].Type);
    }

    private async Task<List<AppEvent>> ReplayFixtureAsync(string path)
    {
        var chunks = LoadFixture(path);
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 4, 26, 3, 30, 0, TimeSpan.Zero));
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
