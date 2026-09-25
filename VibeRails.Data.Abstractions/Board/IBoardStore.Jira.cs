namespace VibeRails.Services.Board;

public partial interface IBoardStore
{
    /// <summary>The connection for this board, or null when the board has none. Never includes a token.</summary>
    Task<BoardJiraConnectionRecord?> GetJiraConnectionAsync(string projectPath, string boardId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every connection whose board still exists, for the root scheduler and token pruning.
    /// Never includes a token.
    /// </summary>
    Task<IReadOnlyList<BoardJiraConnectionRecord>> GetJiraConnectionsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts or replaces the non-secret connection for one board, including its id. A null record
    /// field is stored as given; the caller has already validated it. Returns null, and writes
    /// nothing, when the board does not exist in that project.
    /// </summary>
    Task<BoardJiraConnectionRecord?> SaveJiraConnectionAsync(BoardJiraConnectionRecord connection, CancellationToken cancellationToken = default);

    Task<BoardJiraLinkRecord?> FindJiraLinkAsync(string siteId, string issueId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates the card and its issue link in one transaction (the link's CardId is ignored), so a
    /// crash or a concurrent pull can never leave an unlinked card to be duplicated next time.
    /// Returns null, creating nothing, when that site and issue are already linked.
    /// </summary>
    Task<BoardCardRecord?> CreateJiraCardAsync(string projectPath, NewBoardCard card, BoardJiraLinkRecord link, CancellationToken cancellationToken = default);

    /// <summary>Inserts the link. The card must exist; a second row for the same site and issue is refused.</summary>
    Task AddJiraLinkAsync(BoardJiraLinkRecord link, CancellationToken cancellationToken = default);

    Task UpdateJiraLinkAsync(BoardJiraLinkRecord link, CancellationToken cancellationToken = default);
}
