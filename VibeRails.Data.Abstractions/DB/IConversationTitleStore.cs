namespace VibeRails.DB;

/// <summary>A leased title milestone. Transcript construction happens outside storage transactions.</summary>
public sealed record ConversationTitleWork(string SessionId, string CurrentName,
    string Lease, int Attempts, bool Eligible, long InputId, int Generation, string? LastExcerptHash = null);

/// <summary>Durable title work shared by root backends using the normal state database.</summary>
public interface IConversationTitleStore
{
    /// <summary>Claims one due request for two minutes, excluding work leased by another root.</summary>
    Task<ConversationTitleWork?> ClaimAsync(DateTime now, CancellationToken ct, string? settingsFingerprint = null);
    /// <summary>
    /// Applies a title and its request excerpt hash atomically; null permanently ends work without changing the name.
    /// Explicit names always win. The hash is saved only when the title is applied.
    /// </summary>
    Task<bool> CompleteAsync(ConversationTitleWork work, string? title, DateTime now, CancellationToken ct,
        string? excerptHash = null);
    /// <summary>Ends an owned unchanged refresh while preserving the title, hash and title-change stamp.</summary>
    Task SkipUnchangedRefreshAsync(ConversationTitleWork work, DateTime now, CancellationToken ct);
    /// <summary>Releases owned work until its next retry without changing the session name.</summary>
    Task DeferAsync(ConversationTitleWork work, DateTime retryAt, CancellationToken ct, string? settingsFingerprint = null);
    /// <summary>Returns the latest saved-title change so every root can refresh its own UI.</summary>
    Task<string?> GetChangeStampAsync(CancellationToken ct);
}
