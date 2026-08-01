using System.Globalization;

namespace VibeRails.Services.LlmProxy;

/// <inheritdoc cref="ITokenSaverPauseState"/>
public sealed class TokenSaverPauseState : ITokenSaverPauseState
{
    /// <summary>
    /// The pause window's length in minutes, as the text an agent is shown.
    ///
    /// A string rather than a number because that is the only form that can be shared. The MCP
    /// tools state the window inside <c>[Description]</c> attributes, whose arguments must be
    /// compile-time constants, and no arithmetic on a <see cref="TimeSpan"/> is one — so a numeric
    /// constant would have to be restated as a literal in the prose and could silently drift from
    /// the window actually enforced here. <see cref="PauseWindow"/> parses this, and
    /// <c>TokenSaverTool</c> concatenates it, so both come from this single definition.
    /// </summary>
    public const string PauseWindowMinutesText = "5";

    /// <summary>
    /// The one definition of "a few minutes", derived from <see cref="PauseWindowMinutesText"/> so
    /// the number an agent is told is by construction the number it gets. Shared with the tests.
    /// </summary>
    public static readonly TimeSpan PauseWindow = TimeSpan.FromMinutes(
        int.Parse(PauseWindowMinutesText, CultureInfo.InvariantCulture));

    // UTC ticks rather than a DateTimeOffset field: the value is read on every proxied request and
    // written from MCP tool calls on other threads, and long is what Interlocked/Volatile can carry
    // atomically. 0 means "not paused" — no torn reads, no lock on the request path. UtcTicks
    // specifically, so the round-trip through a bare long cannot lose an offset.
    private long _pausedUntilUtcTicks;

    private readonly TimeProvider _timeProvider;

    // Two ctors rather than a DI-registered TimeProvider, matching WaitingForUserInputObserver:
    // the clock is a test seam, not a service, and nothing else in the app resolves one.
    public TokenSaverPauseState()
        : this(TimeProvider.System)
    {
    }

    public TokenSaverPauseState(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    public DateTimeOffset? PausedUntilUtc
    {
        get
        {
            var ticks = Volatile.Read(ref _pausedUntilUtcTicks);
            if (ticks == 0)
                return null;

            var until = new DateTimeOffset(ticks, TimeSpan.Zero);
            return until > _timeProvider.GetUtcNow() ? until : null;
        }
    }

    public bool IsPaused => PausedUntilUtc is not null;

    public DateTimeOffset Pause()
    {
        // A second call restarts the window rather than extending it cumulatively: an agent that
        // pauses twice gets five more minutes, never ten plus ten.
        var until = _timeProvider.GetUtcNow() + PauseWindow;
        Volatile.Write(ref _pausedUntilUtcTicks, until.UtcTicks);
        return until;
    }

    public void Resume() => Volatile.Write(ref _pausedUntilUtcTicks, 0);
}
