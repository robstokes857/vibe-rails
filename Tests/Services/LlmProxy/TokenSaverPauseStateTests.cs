using Tests.Services.Terminal;
using VibeRails.Services.LlmProxy;
using Xunit;

namespace Tests.Services.LlmProxy;

/// <summary>
/// Pins the pause window's shape. The safety property this feature rests on is that an agent
/// cannot leave compression off: every path back to "not paused" is exercised here, and none of
/// them requires anyone to remember to call Resume.
/// </summary>
public sealed class TokenSaverPauseStateTests
{
    private static readonly DateTimeOffset Start = new(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);

    private static (TokenSaverPauseState State, ManualTimeProvider Clock) Create()
    {
        var clock = new ManualTimeProvider(Start);
        return (new TokenSaverPauseState(clock), clock);
    }

    [Fact]
    public void NotPausedByDefault()
    {
        var (state, _) = Create();

        Assert.False(state.IsPaused);
        Assert.Null(state.PausedUntilUtc);
    }

    [Fact]
    public void Pause_OpensExactlyTheAdvertisedWindow()
    {
        // The MCP tool's description tells the agent "5 minutes". If this number and that sentence
        // ever disagree, the agent is being lied to — hence asserting against the same constant the
        // tool text is written from.
        var (state, _) = Create();

        var until = state.Pause();

        Assert.True(state.IsPaused);
        Assert.Equal(Start + TokenSaverPauseState.PauseWindow, until);
        Assert.Equal(until, state.PausedUntilUtc);
    }

    [Fact]
    public void Pause_LapsesOnItsOwnWithNoResumeCall()
    {
        var (state, clock) = Create();
        state.Pause();

        clock.SetUtcNow(Start + TokenSaverPauseState.PauseWindow - TimeSpan.FromSeconds(1));
        Assert.True(state.IsPaused);

        // Expiry is evaluated on read, so nothing has to fire for this to happen — the state
        // cannot get stuck paused because a timer was dropped, throttled, or never scheduled.
        clock.SetUtcNow(Start + TokenSaverPauseState.PauseWindow);
        Assert.False(state.IsPaused);
        Assert.Null(state.PausedUntilUtc);
    }

    [Fact]
    public void Pause_Again_RestartsTheWindowRatherThanStacking()
    {
        var (state, clock) = Create();
        state.Pause();

        clock.SetUtcNow(Start + TimeSpan.FromMinutes(4));
        var until = state.Pause();

        // Five more minutes from now, not ten from the original call: a confused agent that pauses
        // in a loop still cannot extend the window beyond one window per call.
        Assert.Equal(clock.GetUtcNow() + TokenSaverPauseState.PauseWindow, until);
    }

    [Fact]
    public void Resume_EndsTheWindowImmediately()
    {
        var (state, _) = Create();
        state.Pause();

        state.Resume();

        Assert.False(state.IsPaused);
        Assert.Null(state.PausedUntilUtc);
    }

    [Fact]
    public void Resume_WhenNotPaused_IsANoOp()
    {
        var (state, _) = Create();

        state.Resume();

        Assert.False(state.IsPaused);
    }

    [Fact]
    public void PausedUntilUtc_IsUtc_SoTheWireValueRoundTrips()
    {
        // The expiry crosses a process boundary twice (proxy → app event → browser) and the browser
        // parses it as an absolute instant to run its own countdown. The window is held as a bare
        // long of UTC ticks, so this is what that choice buys: whatever offset the clock reports its
        // time in, the value handed out is the same instant expressed at offset zero, and the route
        // can format it as a Z-suffixed instant without converting anything.
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 7, 31, 12, 0, 0, TimeSpan.FromHours(10)));
        var state = new TokenSaverPauseState(clock);

        state.Pause();

        var until = state.PausedUntilUtc;
        Assert.NotNull(until);
        Assert.Equal(TimeSpan.Zero, until!.Value.Offset);
        Assert.Equal(clock.GetUtcNow().UtcDateTime + TokenSaverPauseState.PauseWindow, until.Value.UtcDateTime);
    }
}
