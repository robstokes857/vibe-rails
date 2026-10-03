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
        var running = (await store.GetRunningAutomationsAsync(projectPath, cancellationToken))
            .Where(run => run.BoardId == request.BoardId).ToList();
        var runningCards = running.Select(run => run.CardId).ToHashSet(StringComparer.Ordinal);
        var live = await liveSessions.GetLiveSessionsAsync(cancellationToken);
        var activity = await store.GetCardActivityAsync(projectPath, request.BoardId,
            request.CardIds.Distinct(StringComparer.Ordinal).ToList(), live.Keys.ToList(), cancellationToken);
        var automationIds = await store.GetAutomationSessionIdsAsync(projectPath,
            activity.Where(a => a.SessionId is not null).Select(a => a.SessionId!).Distinct(StringComparer.Ordinal).ToList(), cancellationToken);
        return new(activity.GroupBy(a => a.CardId).Select(group =>
        {
            // Same rule as the list/detail responses: an Automation's live session blinks the robot
            // but is never the working agent, so it must not report an active session/tab.
            var liveRows = group.Where(a => a.SessionId is not null).ToList();
            var automationRows = liveRows.Where(a => IsAutomationSession(a.Origin, a.SessionId!, automationIds)).ToList();
            var active = liveRows.FirstOrDefault(a => !automationRows.Contains(a) && a.Origin is not ("chat" or "code_review"));
            return new BoardCardActivityResponse(group.Key, active?.SessionId,
                active?.SessionId is { } id ? live[id] : null,
                automationRows.Count > 0 || runningCards.Contains(group.Key));
        }).ToList())
        {
            ActiveAutomationColumnIds = running.Where(run => run.ColumnId is not null)
                .Select(run => run.ColumnId!).Distinct(StringComparer.Ordinal).ToList()
        };
    }
}
