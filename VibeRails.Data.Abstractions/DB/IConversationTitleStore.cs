namespace VibeRails.DB;

/// <summary>A leased title request. Reading the conversation happens outside storage transactions.</summary>
public sealed record ConversationTitleWork(string SessionId, string CurrentName,
    string Lease, int Attempts, bool Eligible, long InputId, int Generation);

/// <summary>Durable title work shared by root backends using the normal state database.</summary>
public interface IConversationTitleStore
{
    /// <summary>
    /// Claims one due request for two minutes, excluding work leased by another root. A conversation
    /// is first due when its user text reaches the milestone or its session ends, then after
    /// every four additional captured user messages following a successful title.
    /// </summary>
    Task<ConversationTitleWork?> ClaimAsync(DateTime now, CancellationToken ct, string? settingsFingerprint = null);
    /// <summary>
    /// Applies a title atomically and completes the milestone; null stops requests without changing
    /// the name. Later user messages can reopen a successful milestone. Explicit names always win.
    /// </summary>
    Task<bool> CompleteAsync(ConversationTitleWork work, string? title, DateTime now, CancellationToken ct);
    /// <summary>Releases owned work until its next retry without changing the session name.</summary>
    Task DeferAsync(ConversationTitleWork work, DateTime retryAt, CancellationToken ct, string? settingsFingerprint = null);
    /// <summary>Returns the latest saved-title change so every root can refresh its own UI.</summary>
    Task<string?> GetChangeStampAsync(CancellationToken ct);
}
