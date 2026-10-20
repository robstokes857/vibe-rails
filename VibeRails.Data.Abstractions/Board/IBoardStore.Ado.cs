namespace VibeRails.Services.Board;

public partial interface IBoardStore
{
    /// <summary>The Azure DevOps connection for this board, or null when the board has none. Never includes a token.</summary>
    Task<BoardAdoConnectionRecord?> GetAdoConnectionAsync(string projectPath, string boardId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every Azure DevOps connection whose board still exists, for the root scheduler and token
    /// pruning. Never includes a token.
    /// </summary>
    Task<IReadOnlyList<BoardAdoConnectionRecord>> GetAdoConnectionsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts or replaces the non-secret connection for one board, including its id. A null record
    /// field is stored as given; the caller has already validated it. Returns null, and writes
    /// nothing, when the board does not exist in that project.
    /// </summary>
    Task<BoardAdoConnectionRecord?> SaveAdoConnectionAsync(BoardAdoConnectionRecord connection, CancellationToken cancellationToken = default);

    /// <summary>Removes only the matching connection. Keeps the board, cards and historical work item links.</summary>
    Task<bool> DeleteAdoConnectionAsync(string projectPath, string boardId, string connectionId, CancellationToken cancellationToken = default);

    /// <summary>Atomically gives a connection its own board ("Azure DevOps · {team or query}"),
    /// cloning lanes and moving only its imported cards from the source board. Preserves identities
    /// and skips lane Automations. The board starts unpublished. A source board that would be
    /// left empty and unused becomes that board itself, keeping its name, unless
    /// <paramref name="newBoard"/> asks for a new one (see the Jira version). Repeated calls for
    /// the same connection return the same destination. Once a query's name is known, replaces its
    /// provisional project label only while the board still has that generated label.</summary>
    Task<BoardAdoConnectionRecord> EnsureDedicatedAdoBoardAsync(string projectPath, string connectionId, CancellationToken cancellationToken = default, bool newBoard = false);

    Task<BoardAdoLinkRecord?> FindAdoLinkAsync(string connectionId, int workItemId, CancellationToken cancellationToken = default);

    /// <summary>Retained work item identities for a live card in this project, including after a lane/board move.</summary>
    Task<IReadOnlyList<BoardAdoLinkRecord>> GetAdoLinksForCardAsync(string projectPath, string cardId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates the card and its work item link in one transaction (the link's CardId is ignored),
    /// so a crash or a concurrent pull can never leave an unlinked card to be duplicated next time.
    /// Returns null, creating nothing, when that connection and work item are already linked.
    /// </summary>
    Task<BoardCardRecord?> CreateAdoCardAsync(string projectPath, NewBoardCard card, BoardAdoLinkRecord link, CancellationToken cancellationToken = default);

    /// <summary>Inserts the link. The card must exist; a second row for the same connection and work item is refused.</summary>
    Task AddAdoLinkAsync(BoardAdoLinkRecord link, CancellationToken cancellationToken = default);

    Task UpdateAdoLinkAsync(BoardAdoLinkRecord link, CancellationToken cancellationToken = default);

    /// <summary>Reads bounded pending events; callers serialize delivery with the Azure DevOps lock.</summary>
    Task<IReadOnlyList<BoardAdoDelivery>> GetPendingAdoDeliveriesAsync(CancellationToken cancellationToken = default);
    /// <summary>Reads local delivery status for a live card in its owning project.</summary>
    Task<IReadOnlyList<BoardAdoDelivery>> GetAdoDeliveriesAsync(string projectPath, string cardId, CancellationToken cancellationToken = default);
    /// <summary>Compare-and-set transition; a crash during sending must never cause a blind repost.</summary>
    Task<bool> SetAdoDeliveryAsync(string id, string expectedStatus, string status, string? message, string? url, CancellationToken cancellationToken = default);
    /// <summary>After acquiring the cross-process lock, surface interrupted writes without retrying them.</summary>
    Task RecoverAdoDeliveriesAsync(CancellationToken cancellationToken = default);
}
