namespace VibeRails.Services.Terminal;

/// <summary>
/// Process-local Automation terminal idle monitor. Implementations track only explicitly
/// registered Automation sessions; ordinary interactive terminals are outside this lifecycle.
/// </summary>
public interface IAutomationConsumer
{
    /// <summary>Cancelled when a registered session produces no PTY output for two minutes.</summary>
    CancellationToken IdleShutdownToken { get; }

    /// <summary>Whether this Job host received an idle-shutdown request.</summary>
    bool IdleShutdownRequested { get; }

    /// <summary>The first session whose inactivity requested shutdown, for diagnostics.</summary>
    string? IdleSessionId { get; }

    /// <summary>Registers a session and returns its raw PTY-output consumer.</summary>
    ITerminalConsumer RegisterSession(string sessionId);
}
