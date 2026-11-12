namespace VibeRails.Services.Terminal;

/// <summary>
/// The lock on a relay-attached session's remote viewer. A viewer is unlocked only by entering
/// the Remote Access PIN. "No PIN configured" means locked, never open: a session is only offered
/// to the relay while a PIN exists (<c>RemoteConfig.IsEnabled</c>), so the PIN can be missing
/// later only because it was cleared, and clearing it must not hand a live session to whoever
/// reaches it through the relay. VIBE-310.
/// </summary>
internal static class RemoteViewerLock
{
    public const int Locked = 0;
    public const int Unlocked = 1;

    /// <summary>The state a new, disconnected or re-attached viewer starts in.</summary>
    public const int Initial = Locked;

    /// <summary>What <paramref name="state"/> is worth on the next remote frame.</summary>
    public static int Current(int state, bool pinConfigured) =>
        pinConfigured && state == Unlocked ? Unlocked : Locked;
}
