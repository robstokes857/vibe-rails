using VibeRails.Services.Launches;
using VibeRails.Utils;

namespace VibeRails.Services.Board;

public sealed partial class BoardService
{
    private async Task<IReadOnlyList<BoardRunningAutomation>> GetRunningLaunchesAsync(string project, CancellationToken ct)
    {
        if (launchRuns is null) return [];
        var entries = await store.GetLaunchEventsAsync(project, ct: ct);
        var result = new List<BoardRunningAutomation>();
        foreach (var run in await launchRuns.GetActiveRunsAsync(ct))
        {
            var context = run.Snapshot.Context;
            if (!ProjectPathComparer.Matches(project, run.ProjectPath) || context.CardId is null
                || run.Status == "queued" || context.TriggerKind != "board-lane" && context.SessionOrigin is not ("automation" or "lane_review" or "code_review")) continue;
            var card = await store.FindCardAsync(project, context.CardId, ct);
            if (card is null) continue;
            result.Add(new(card.Id, card.BoardId, entries.FirstOrDefault(e => e.Id == context.LaneEntryId)?.ColumnId, run.Id));
        }
        return result;
    }

    private async Task<IReadOnlyList<string>> GetWaitingLaunchCardsAsync(string project, IReadOnlyList<string> cardIds, CancellationToken ct)
    {
        if (cardIds.Count == 0) return [];
        var waiting = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in await store.GetLaunchEventsAsync(project, ct: ct))
        {
            if (!entry.Current || entry.SkipRequested || !cardIds.Contains(entry.CardId)) continue;
            var run = entry.RunId is null || launchRuns is null ? null : await launchRuns.GetRunAsync(entry.RunId, ct);
            if (entry.RunId is null || run?.Status == "queued") waiting.Add(entry.CardId);
        }
        return waiting.ToArray();
    }
}
