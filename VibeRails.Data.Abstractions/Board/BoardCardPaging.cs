namespace VibeRails.Services.Board;

public sealed record BoardCardPageQuery(
    int PageSize = 30,
    string? ColumnId = null,
    int Offset = 0,
    string? Q = null,
    string? Assignee = null,
    string? Type = null,
    string? Priority = null,
    string? Tag = null);

public sealed record BoardCardLanePage(string ColumnId, int TotalCount, int FilteredCount, int NextOffset, bool HasMore);

public sealed record BoardCardPage(
    IReadOnlyList<BoardCardRecord> Cards,
    IReadOnlyList<BoardCardLanePage> Lanes,
    IReadOnlyList<string> Assignees,
    IReadOnlyList<string> Tags,
    int TotalCount,
    int FilteredCount,
    int BlockedCount,
    long RemainingPoints);

public partial interface IBoardStore
{
    /// <summary>
    /// With no column, reads all open cards and the first page of each completed lane.
    /// Column requests page that lane. Counts and filter choices cover the entire board.
    /// </summary>
    Task<BoardCardPage> GetCardsPageAsync(string projectPath, BoardCardPageQuery query,
        CancellationToken cancellationToken = default, string? boardId = null);
}
