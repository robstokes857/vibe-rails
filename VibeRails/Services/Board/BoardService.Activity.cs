using System.Security.Cryptography;
using System.Text;
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
        // Read before anything else (VIBE-322): this response, and whatever a client rereads
        // because the stamp moved, is then at least as new as the stamp it carries.
        var changeToken = await store.GetChangeStampAsync(cancellationToken);
        var running = (await GetRunningLaunchesAsync(projectPath, cancellationToken))
            .Where(run => run.BoardId == request.BoardId).ToList();
        var runningCards = running.Select(run => run.CardId).ToHashSet(StringComparer.Ordinal);
        var live = await liveSessions.GetLiveSessionsAsync(cancellationToken);
        var activity = await store.GetCardActivityAsync(projectPath, request.BoardId,
            request.CardIds.Distinct(StringComparer.Ordinal).ToList(), live.Keys.ToList(), cancellationToken);
        var waiting = (await GetWaitingLaunchCardsAsync(projectPath,
            activity.Select(a => a.CardId).Distinct(StringComparer.Ordinal).ToList(), cancellationToken))
            .ToHashSet(StringComparer.Ordinal);
        var automationIds = await store.GetAutomationSessionIdsAsync(projectPath,
            activity.Where(a => a.SessionId is not null).Select(a => a.SessionId!).Distinct(StringComparer.Ordinal).ToList(), cancellationToken);
        var desktopCards = await store.GetDesktopActivityAsync(projectPath,
            activity.Select(a => a.CardId).Distinct(StringComparer.Ordinal).ToList(), DateTime.UtcNow, cancellationToken);
        return new(activity.GroupBy(a => a.CardId).Select(group =>
        {
            // Same rule as the list/detail responses: an Automation's live session blinks the robot
            // but is never the working agent, so it must not report an active session/tab.
            var liveRows = group.Where(a => a.SessionId is not null).ToList();
            var automationRows = liveRows.Where(a => IsAutomationSession(a.Origin, a.SessionId!, automationIds)).ToList();
            var agentWorkflow = group.First().WorkflowMode == BoardWorkflowModes.Agent;
            var active = liveRows.FirstOrDefault(a => !automationRows.Contains(a) && IsWorkflowAgent(a.Origin ?? "", agentWorkflow));
            var desktop = desktopCards?.GetValueOrDefault(group.Key);
            var card = new BoardCardActivityResponse(group.Key, active?.SessionId,
                active?.SessionId is { } id ? live[id] : null,
                automationRows.Count > 0 || runningCards.Contains(group.Key), waiting.Contains(group.Key))
            {
                HasActiveDesktopAgent = desktop?.IsActive == true,
                DesktopAppName = desktop?.AppName
            };
            return card with { Stamp = CardStamp(changeToken, card, liveRows.Select(a => $"{a.SessionId}={live[a.SessionId!]}")) };
        }).ToList())
        {
            ActiveAutomationColumnIds = running.Where(run => run.ColumnId is not null)
                .Select(run => run.ColumnId!).Distinct(StringComparer.Ordinal).ToList(),
            ChangeToken = changeToken
        };
    }

    /// <summary>
    /// What the card editor's refresh shows for one card, as a short hash (VIBE-322): the Board change
    /// stamp covers card, discussion and rail rows; the rest is the state that changes without a
    /// board.db write (live tabs, running and waiting runs, desktop leases expiring).
    /// </summary>
    internal static string? CardStamp(string? changeToken, BoardCardActivityResponse card, IEnumerable<string> liveSessions)
    {
        if (changeToken is null) return null;
        var text = string.Join('\n', new[] { changeToken, card.Id, card.ActiveSessionId ?? "", card.ActiveTabId ?? "",
            card.HasActiveAutomation ? "1" : "0", card.HasWaitingAutomation ? "1" : "0", card.HasActiveDesktopAgent ? "1" : "0",
            card.DesktopAppName ?? "" }
            .Concat(liveSessions));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)), 0, 8).ToLowerInvariant();
    }
}
