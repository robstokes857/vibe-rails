namespace VibeRails.Services.LlmProxy;

/// <summary>
/// A short, self-expiring "stop compressing" window for the process that hosts the LLM proxy.
///
/// An agent that hits an elision marker (<c>[... N lines elided ...]</c>, <c>[xN]</c>,
/// <c>[... N passed ...]</c>) has no way to recover what was removed. The MCP token-saver tools let
/// it open this window, re-run the command, and read the output verbatim.
///
/// The pause deliberately lives in memory, in this process, and nowhere else:
/// <list type="bullet">
/// <item>The proxy runs in the terminal tab's own child <c>vb.exe</c>, so process-local state is
/// <b>per-tab by construction</b> — no tab id to plumb, no shared file to key or garbage-collect,
/// and one agent's pause can never disable another tab's saver.</item>
/// <item>It keeps SQLite off the relay path (see the TokenSaver README: the relay must never wait
/// on the database), and keeps ephemeral runtime state out of settings.json, where it would race
/// the settings UI's read-modify-write save.</item>
/// <item>It dies with the tab, which is the correct lifetime: a pause must not outlive the agent
/// that asked for it.</item>
/// </list>
///
/// Expiry is evaluated on read rather than by a timer, so there is no callback to leak and no state
/// that can get stuck paused if a resume is never sent. The window is a fixed
/// <see cref="TokenSaverPauseState.PauseWindow"/> — long enough to re-run a command and read the
/// result, short enough that a forgotten pause costs little.
/// </summary>
public interface ITokenSaverPauseState
{
    /// <summary>When the current pause lapses, or null when the saver is not paused.</summary>
    DateTimeOffset? PausedUntilUtc { get; }

    bool IsPaused { get; }

    /// <summary>Starts (or restarts) the window. Returns the new expiry.</summary>
    DateTimeOffset Pause();

    /// <summary>Ends the window early. A no-op when not paused.</summary>
    void Resume();
}
