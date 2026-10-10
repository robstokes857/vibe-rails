namespace VibeRails.Services.Board;

public partial interface IBoardStore
{
    /// <summary>
    /// Starts or refreshes a desktop MCP client's activity on a live, open card in the project.
    /// The host supplies an opaque client ID and a future lease expiry. A new explicit start
    /// may reactivate a previously ended lease. Returns false for an unavailable or closed card.
    /// </summary>
    Task<bool> StartDesktopActivityAsync(string projectPath, string cardId, string clientId,
        string clientLabel, DateTime expiresUtc, CancellationToken cancellationToken = default);

    /// <summary>Renews only this host-generated client's unexpired, non-ended leases.</summary>
    Task RenewDesktopActivityAsync(string clientId, DateTime expiresUtc,
        CancellationToken cancellationToken = default);

    /// <summary>Ends a client's activity on one card, or all its cards when cardId is null, retaining its rows.</summary>
    Task EndDesktopActivityAsync(string clientId, string? cardId,
        CancellationToken cancellationToken = default);

    /// <summary>Returns only requested live, open cards in the project that have an unexpired desktop lease.</summary>
    Task<IReadOnlySet<string>> GetDesktopActiveCardIdsAsync(string projectPath,
        IReadOnlyList<string> cardIds, DateTime nowUtc, CancellationToken cancellationToken = default);
}
