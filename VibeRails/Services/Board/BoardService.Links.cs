using VibeRails.DTOs;

namespace VibeRails.Services.Board;

public partial interface IBoardService
{
    /// <summary>Searches related-card candidates, optionally limiting eligibility to the source project. An omitted source keeps already-linked cards eligible for merge/reference pickers.</summary>
    Task<BoardCardLinkCandidatesResponse?> GetCardLinkCandidatesAsync(string projectPath, string? idOrKey, string? query,
        CancellationToken cancellationToken = default, string? preferredProjectPath = null, bool currentProjectOnly = false);
    Task<BoardLinkedCardDto?> LinkCardAsync(string projectPath, string idOrKey, string? targetIdOrKey, CancellationToken cancellationToken = default);
    Task<bool> UnlinkCardAsync(string projectPath, string idOrKey, string targetIdOrKey, CancellationToken cancellationToken = default);
}

public sealed partial class BoardService
{
    public async Task<BoardCardLinkCandidatesResponse?> GetCardLinkCandidatesAsync(
        string projectPath, string? idOrKey, string? query, CancellationToken cancellationToken = default,
        string? preferredProjectPath = null, bool currentProjectOnly = false)
    {
        var search = query?.Trim() ?? string.Empty;
        if (search.Length > MaxTitleLength)
            throw new BoardValidationException($"Search must be {MaxTitleLength} characters or fewer.");
        if (searchService is not null)
        {
            var source = idOrKey is null ? null : await store.FindCardAsync(projectPath, idOrKey, cancellationToken);
            if (idOrKey is not null && source is null) return null;
            var excluded = source is null ? [] : await store.GetLinkedCardIdsAsync(projectPath, source.Id, cancellationToken);
            var hits = await searchService.SearchAsync(preferredProjectPath ?? projectPath, search, excludeCardId: source?.Id,
                ct: cancellationToken, excludedCardIds: excluded, projectScope: currentProjectOnly ? projectPath : null);
            return new(hits.Select(hit => new BoardLinkedCardDto(hit.Id, hit.Key, hit.Title, hit.BoardId,
                hit.BoardName, hit.ColumnId, hit.ColumnName, hit.DisplayId, hit.ProjectPath, hit.IsCurrentProject)).ToList());
        }
        var cards = await store.GetCardLinkCandidatesAsync(projectPath, idOrKey, search, cancellationToken, preferredProjectPath, currentProjectOnly);
        return cards is null ? null : new BoardCardLinkCandidatesResponse(cards.Select(card => ToDto(card, preferredProjectPath ?? projectPath)).ToList());
    }

    public async Task<BoardLinkedCardDto?> LinkCardAsync(
        string projectPath, string idOrKey, string? targetIdOrKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(targetIdOrKey))
            throw new BoardValidationException("Choose a card to link.");
        var linked = await store.LinkCardAsync(projectPath, idOrKey, targetIdOrKey.Trim(), cancellationToken);
        return linked is null ? null : ToDto(linked, projectPath);
    }

    public Task<bool> UnlinkCardAsync(string projectPath, string idOrKey, string targetIdOrKey, CancellationToken cancellationToken = default) =>
        store.UnlinkCardAsync(projectPath, idOrKey, targetIdOrKey, cancellationToken);

    private static BoardLinkedCardDto ToDto(BoardLinkedCardRecord card, string projectPath) =>
        new(card.Id, card.Key, card.Title, card.BoardId, card.BoardName, card.ColumnId, card.ColumnName, card.DisplayId,
            card.ProjectPath, card.ProjectPath is null || string.Equals(card.ProjectPath,
                BoardPaths.NormalizeProjectPath(projectPath), BoardPaths.ProjectPathComparison));
}
