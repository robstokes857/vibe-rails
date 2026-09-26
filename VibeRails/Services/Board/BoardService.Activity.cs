using VibeRails.DTOs;

namespace VibeRails.Services.Board;

public partial interface IBoardService
{
    Task<BoardCardActivityListResponse> GetCardActivityAsync(string projectPath, BoardCardActivityRequest request,
        CancellationToken cancellationToken = default);
}

public sealed partial class BoardService
{
    public const int MaxActivityCardIds = 100;

    public async Task<BoardCardActivityListResponse> GetCardActivityAsync(string projectPath, BoardCardActivityRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.BoardId) || request.BoardId.Length > 100
            || request.CardIds is null || request.CardIds.Count > MaxActivityCardIds
            || request.CardIds.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 100))
            throw new BoardValidationException("Supply a board ID and up to 100 card IDs.");
        if (request.CardIds.Count == 0) return new([]);
        var live = await liveSessions.GetLiveSessionsAsync(cancellationToken);
        var activity = await store.GetCardActivityAsync(projectPath, request.BoardId,
            request.CardIds.Distinct(StringComparer.Ordinal).ToList(), live.Keys.ToList(), cancellationToken);
        var automationIds = await store.GetAutomationSessionIdsAsync(projectPath,
            activity.Where(a => a.SessionId is not null).Select(a => a.SessionId!).Distinct(StringComparer.Ordinal).ToList(), cancellationToken);
        return new(activity.GroupBy(a => a.CardId).Select(group =>
        {
            var active = group.FirstOrDefault(a => a.SessionId is not null);
            return new BoardCardActivityResponse(group.Key, active?.SessionId,
                active?.SessionId is { } id ? live[id] : null,
                group.Any(a => a.SessionId is not null
                    && (a.Origin == BoardSessionRecord.AutomationOrigin || automationIds.Contains(a.SessionId))));
        }).ToList());
    }
}
