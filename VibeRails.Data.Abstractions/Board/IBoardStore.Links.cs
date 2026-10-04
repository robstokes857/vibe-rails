namespace VibeRails.Services.Board;

public partial interface IBoardStore
{
    /// <summary>Searches candidates before the result limit, optionally within the source project. An omitted source does not exclude existing relationships.</summary>
    Task<IReadOnlyList<BoardLinkedCardRecord>?> GetCardLinkCandidatesAsync(string projectPath, string? idOrKey, string query,
        CancellationToken cancellationToken = default, string? preferredProjectPath = null, bool currentProjectOnly = false);
    /// <summary>Reads linked identities for a live project-scoped source, without loading its discussion or attachments.</summary>
    Task<IReadOnlyList<string>> GetLinkedCardIdsAsync(string projectPath, string cardId, CancellationToken cancellationToken = default);
    /// <summary>Links a project-scoped source to any live local card in both directions. Foreign targets require a row ID or full permanent key; aliases stay project-scoped. Repeated links are idempotent.</summary>
    Task<BoardLinkedCardRecord?> LinkCardAsync(string projectPath, string idOrKey, string targetIdOrKey, CancellationToken cancellationToken = default);
    Task<bool> UnlinkCardAsync(string projectPath, string idOrKey, string targetIdOrKey, CancellationToken cancellationToken = default);
}
