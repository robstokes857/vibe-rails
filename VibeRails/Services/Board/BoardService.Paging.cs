using VibeRails.DTOs;

namespace VibeRails.Services.Board;

public partial interface IBoardService
{
    Task<BoardCardListResponse> GetCardsPageAsync(string projectPath, BoardCardPageQuery query,
        CancellationToken cancellationToken = default, string? boardId = null);
}

public sealed partial class BoardService
{
    public async Task<BoardCardListResponse> GetCardsPageAsync(string projectPath, BoardCardPageQuery query,
        CancellationToken cancellationToken = default, string? boardId = null)
    {
        var search = query.Q?.Trim();
        if (search?.Length > 300) throw new BoardValidationException("Search must be 300 characters or fewer.");
        if (query.Tag?.Length > 40) throw new BoardValidationException("Tag must be 40 characters or fewer.");
        var page = await store.GetCardsPageAsync(projectPath, query with
        {
            PageSize = Math.Clamp(query.PageSize, 1, 100),
            Offset = Math.Max(0, query.Offset),
            ColumnId = string.IsNullOrWhiteSpace(query.ColumnId) ? null : query.ColumnId.Trim(),
            Q = search,
            Assignee = NormalizeAssignee(query.Assignee),
            Type = NormalizeCardType(query.Type),
            Priority = NormalizePriority(query.Priority),
            Tag = query.Tag?.Trim()
        }, cancellationToken, NormalizeBoardId(boardId));
        var response = await GetCardListResponseAsync(projectPath, page.Cards, cancellationToken);
        return response with
        {
            Lanes = page.Lanes,
            Assignees = page.Assignees,
            Tags = page.Tags,
            TotalCount = page.TotalCount,
            FilteredCount = page.FilteredCount,
            BlockedCount = page.BlockedCount,
            RemainingPoints = page.RemainingPoints
        };
    }
}
