namespace VibeRails.Services.Board;

public partial interface IBoardStore
{
    Task<IReadOnlyList<BoardLinkedCardRecord>?> GetCardLinkCandidatesAsync(string projectPath, string idOrKey, string query, CancellationToken cancellationToken = default);
    /// <summary>Links two cards in the project in both directions. Repeated links are idempotent.</summary>
    Task<BoardLinkedCardRecord?> LinkCardAsync(string projectPath, string idOrKey, string targetIdOrKey, CancellationToken cancellationToken = default);
    Task<bool> UnlinkCardAsync(string projectPath, string idOrKey, string targetIdOrKey, CancellationToken cancellationToken = default);
}
