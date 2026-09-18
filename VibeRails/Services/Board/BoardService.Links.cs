using VibeRails.DTOs;

namespace VibeRails.Services.Board;

public partial interface IBoardService
{
    Task<BoardCardLinkCandidatesResponse?> GetCardLinkCandidatesAsync(string projectPath, string idOrKey, string? query, CancellationToken cancellationToken = default);
    Task<BoardLinkedCardDto?> LinkCardAsync(string projectPath, string idOrKey, string? targetIdOrKey, CancellationToken cancellationToken = default);
    Task<bool> UnlinkCardAsync(string projectPath, string idOrKey, string targetIdOrKey, CancellationToken cancellationToken = default);
}

public sealed partial class BoardService
{
    public async Task<BoardCardLinkCandidatesResponse?> GetCardLinkCandidatesAsync(
        string projectPath, string idOrKey, string? query, CancellationToken cancellationToken = default)
    {
        var search = query?.Trim() ?? string.Empty;
        if (search.Length > MaxTitleLength)
            throw new BoardValidationException($"Search must be {MaxTitleLength} characters or fewer.");
        var cards = await store.GetCardLinkCandidatesAsync(projectPath, idOrKey, search, cancellationToken);
        return cards is null ? null : new BoardCardLinkCandidatesResponse(cards.Select(ToDto).ToList());
    }

    public async Task<BoardLinkedCardDto?> LinkCardAsync(
        string projectPath, string idOrKey, string? targetIdOrKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(targetIdOrKey))
            throw new BoardValidationException("Choose a card to link.");
        var linked = await store.LinkCardAsync(projectPath, idOrKey, targetIdOrKey.Trim(), cancellationToken);
        return linked is null ? null : ToDto(linked);
    }

    public Task<bool> UnlinkCardAsync(string projectPath, string idOrKey, string targetIdOrKey, CancellationToken cancellationToken = default) =>
        store.UnlinkCardAsync(projectPath, idOrKey, targetIdOrKey, cancellationToken);

    private static BoardLinkedCardDto ToDto(BoardLinkedCardRecord card) =>
        new(card.Id, card.Key, card.Title, card.BoardId, card.BoardName, card.ColumnId, card.ColumnName);
}
