using System.Text.Json.Serialization.Metadata;
using VibeRails.DTOs;
using VibeRails.Interfaces;
using VibeRails.Services;
using VibeRails.Services.Terminal;
using Xunit;

namespace Tests.Services.Terminal;

/// <summary>
/// Behavior tests for the chunk-repetition based waiting detection.
///
/// The observer fires `session_waiting_for_user` when codex's PTY output
/// settles into the idle-spinner pattern: many copies of one or two distinct
/// raw ANSI chunks (codex's row-clear / cursor-position keep-alive frames)
/// dominate a 5-second rolling window. Working state is rejected because
/// codex's per-character "Working" animation generates dozens of unique
/// chunks per 5s window.
///
/// End-to-end fixture-based regressions live in:
///   - Session_0ebd404d_WaitingObserverRegressionTests
///   - Session_881cd29d_WaitingObserverRegressionTests
///   - Session_e910eb94_WaitingObserverRegressionTests
/// </summary>
public sealed class WaitingForUserInputObserverTests
{
    private const string CodexIdleChunk =
        "\x1b[?25l\x1b[15;2H\x1b[K\x1b[16;62H\x1b[K\x1b[17;2H\x1b[K\n\x1b[K\x1b[19;47H\x1b[K\x1b[20;2H\x1b[K\x1b[21;77H\x1b[K\x1b[22;2H\x1b[K\x1b[23;56H\x1b[K\x1b[24;2H\x1b[K\x1b[25;22H\x1b[K\x1b[?25h";

    [Fact]
    public async Task NonCodexSession_IsIgnoredEvenWithRepeatingPattern()
    {
        var clock = new ManualTimeProvider();
        var (observer, events) = CreateObserver(clock);
        await StartSessionAsync(observer, "session-1", "claude", clock);

        // Inject 60 copies of the codex idle chunk over 3 seconds.
        for (var i = 0; i < 60; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(50));
            await SendOutputAsync(observer, "session-1", CodexIdleChunk);
        }

        Assert.Empty(events);
    }

    [Fact]
    public async Task SingleChunk_DoesNotFire()
    {
        var clock = new ManualTimeProvider();
        var (observer, events) = CreateObserver(clock);
        await StartSessionAsync(observer, "session-1", "codex", clock);

        await SendOutputAsync(observer, "session-1", CodexIdleChunk);

        Assert.Empty(events);
    }

    [Fact]
    public async Task RepeatingIdleChunks_FireExactlyOnce()
    {
        var clock = new ManualTimeProvider();
        var (observer, events) = CreateObserver(clock);
        await StartSessionAsync(observer, "session-1", "codex", clock);

        // ~80 copies of the idle chunk over ~4 seconds — well past the
        // 40-sample threshold, comfortably under the 5s window.
        for (var i = 0; i < 80; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(50));
            await SendOutputAsync(observer, "session-1", CodexIdleChunk);
        }

        Assert.Single(events);
        Assert.Equal("session_waiting_for_user", events[0].Type);
    }

    [Fact]
    public async Task DiverseBigChunks_DoNotFire_EvenAtHighRate()
    {
        var clock = new ManualTimeProvider();
        var (observer, events) = CreateObserver(clock);
        await StartSessionAsync(observer, "session-1", "codex", clock);

        // 200 chunks each unique (working state simulation) over ~4s.
        for (var i = 0; i < 200; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(20));
            // Vary the cursor row to make each chunk distinct (>=50 bytes).
            var unique = $"\x1b[?25l\x1b[{10 + (i % 30)};{2 + i % 50}H\x1b[K\x1b[{20 + i % 5};{i % 100}H\x1b[K\x1b[{i % 25};2H\x1b[K\x1b[?25h{i}";
            await SendOutputAsync(observer, "session-1", unique);
        }

        Assert.Empty(events);
    }

    [Fact]
    public async Task IdleThenWorkingThenIdle_FiresTwice()
    {
        var clock = new ManualTimeProvider();
        var (observer, events) = CreateObserver(clock);
        await StartSessionAsync(observer, "session-1", "codex", clock);

        // First idle window — fires once
        for (var i = 0; i < 80; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(50));
            await SendOutputAsync(observer, "session-1", CodexIdleChunk);
        }
        Assert.Single(events);

        // Codex resumes working — diverse chunks for >5s flush the buffer.
        for (var i = 0; i < 200; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(30));
            var unique = $"\x1b[?25l\x1b[{10 + (i % 30)};{2 + i % 50}H\x1b[K\x1b[{20 + i % 5};{i % 100}H\x1b[K\x1b[{i % 25};2H\x1b[K\x1b[?25h{i}";
            await SendOutputAsync(observer, "session-1", unique);
        }
        Assert.Single(events);

        // Codex settles back into idle. Phase 3 must run for at least one
        // full SampleWindow so the rolling buffer ages out the working
        // residuals before classifying as Idle: the small-chunk
        // concentration gate added for Session_e910eb94's CUP-park false
        // positive will keep returning Working until those varied small
        // working chunks fall out of the window. Derive the iteration count
        // from SampleWindow so this test self-adjusts if the observer's
        // window is retuned.
        const int phase3StepMs = 50;
        var phase3Iterations =
            (int)Math.Ceiling(WaitingForUserInputObserver.SampleWindow.TotalMilliseconds / phase3StepMs) + 30;
        for (var i = 0; i < phase3Iterations; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(phase3StepMs));
            await SendOutputAsync(observer, "session-1", CodexIdleChunk);
        }

        Assert.Equal(2, events.Count);
        Assert.All(events, e => Assert.Equal("session_waiting_for_user", e.Type));
    }

    [Fact]
    public async Task Submit_ClearsPreSubmitWindowBeforeAnotherWaitCanFire()
    {
        var clock = new ManualTimeProvider();
        var (observer, events) = CreateObserver(clock);
        await StartSessionAsync(observer, "session-1", "codex", clock);

        // Reach the first genuine wait.
        for (var i = 0; i < 45; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(50));
            await SendOutputAsync(observer, "session-1", CodexIdleChunk);
        }
        Assert.Single(events);

        // A brief diverse small-chunk repaint releases the old once-per-idle
        // gate while the rolling window still contains the pre-submit idle
        // frames. This is the shape that used to let stale frames reassert
        // WAITING immediately after Enter.
        for (var i = 0; i < 7; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(10));
            await SendOutputAsync(observer, "session-1", $"\x1b[{i + 1}H");
        }

        await SendInputAsync(observer, "session-1", "\r");

        // Repetition immediately after Enter must not be combined with the old
        // idle samples to create a second event.
        for (var i = 0; i < 12; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(10));
            await SendOutputAsync(observer, "session-1", "\x1b[1H");
        }
        Assert.Single(events);

        // The reset is not a permanent suppression: a fresh, fully observed
        // idle period can still announce the next real prompt.
        for (var i = 0; i < 55; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(50));
            await SendOutputAsync(observer, "session-1", CodexIdleChunk);
        }
        Assert.Equal(2, events.Count);
    }

    [Fact]
    public async Task Submit_CannotResetGenerationBetweenWaitCheckAndPublish()
    {
        var clock = new ManualTimeProvider();
        var bus = new BlockingAppEventBus();
        var observer = new WaitingForUserInputObserver(bus, clock);
        await StartSessionAsync(observer, "session-1", "codex", clock);

        // Stop immediately before the first eligible classification (oldest span is 1.95s).
        for (var i = 0; i < 40; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(50));
            await SendOutputAsync(observer, "session-1", CodexIdleChunk);
        }

        clock.Advance(TimeSpan.FromMilliseconds(50));
        var publishing = Task.Run(
            async () => await SendOutputAsync(observer, "session-1", CodexIdleChunk),
            TestContext.Current.CancellationToken);
        await bus.PublishEntered.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        var submitStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var submit = Task.Run(async () =>
        {
            submitStarted.TrySetResult();
            await SendInputAsync(observer, "session-1", "\r");
        }, TestContext.Current.CancellationToken);
        await submitStarted.Task;

        // Publication holds the same session-buffer lock as ResetAfterSubmit, so Enter cannot
        // invalidate the generation and complete before this already-approved event is delivered.
        try
        {
            var earlyCompletion = await Task.WhenAny(
                submit,
                Task.Delay(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken));
            Assert.NotSame(submit, earlyCompletion);
        }
        finally
        {
            bus.AllowPublish.Set();
        }

        await Task.WhenAll(publishing, submit);
        Assert.Equal(1, bus.PublishCount);
    }

    private static (WaitingForUserInputObserver Observer, List<AppEvent> Events) CreateObserver(TimeProvider clock)
    {
        var bus = new AppEventBus();
        var observer = new WaitingForUserInputObserver(bus, clock);
        var events = new List<AppEvent>();
        bus.Subscribe(events.Add);
        return (observer, events);
    }

    private static async Task StartSessionAsync(
        WaitingForUserInputObserver observer,
        string sessionId,
        string cli,
        TimeProvider clock)
    {
        await observer.OnSessionStartAsync(new TerminalSessionStartEvent(
            SessionId: sessionId,
            Cli: cli,
            WorkDir: string.Empty,
            EnvName: null,
            SetupCommands: Array.Empty<string>(),
            LaunchCommand: string.Empty,
            TimestampUtc: clock.GetUtcNow()));
    }

    private static async Task SendOutputAsync(
        WaitingForUserInputObserver observer,
        string sessionId,
        string chunk)
    {
        await observer.OnTerminalIoAsync(new TerminalIoEvent(
            SessionId: sessionId,
            Direction: TerminalIoDirection.Output,
            Source: TerminalIoSource.Pty,
            Text: chunk,
            TimestampUtc: DateTimeOffset.UtcNow));
    }

    private static async Task SendInputAsync(
        WaitingForUserInputObserver observer,
        string sessionId,
        string input)
    {
        await observer.OnTerminalIoAsync(new TerminalIoEvent(
            SessionId: sessionId,
            Direction: TerminalIoDirection.Input,
            Source: TerminalIoSource.LocalWebUi,
            Text: input,
            TimestampUtc: DateTimeOffset.UtcNow));
    }

    private sealed class BlockingAppEventBus : IAppEventBus
    {
        public TaskCompletionSource PublishEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim AllowPublish { get; } = new(initialState: false);
        public int PublishCount { get; private set; }

        public void Publish<T>(string type, T payload, JsonTypeInfo<T> typeInfo)
        {
            PublishEntered.TrySetResult();
            AllowPublish.Wait();
            PublishCount++;
        }

        public void Publish(AppEvent appEvent) =>
            throw new NotSupportedException();

        public IDisposable Subscribe(Action<AppEvent> listener) =>
            throw new NotSupportedException();
    }
}
