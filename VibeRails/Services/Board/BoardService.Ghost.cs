namespace VibeRails.Services.Board;

public partial interface IBoardService
{
    /// <summary>Checks only this card's linked code, preserving its state if verification is unavailable.</summary>
    Task<BoardGhostCheckResponse?> RecheckGhostAsync(string projectPath, string idOrKey, CancellationToken cancellationToken = default);
}

public sealed partial class BoardService
{
    public async Task<BoardGhostCheckResponse?> RecheckGhostAsync(string projectPath, string idOrKey, CancellationToken cancellationToken = default)
    {
        var card = await store.FindCardAsync(projectPath, idOrKey, cancellationToken);
        if (card is null) return null;
        if (ghosts is null) return new("unverified", "Code landing checks are unavailable in this host.", card.GhostReason);
        return await ghosts.RecheckAsync(projectPath, card.Id, cancellationToken);
    }
}
