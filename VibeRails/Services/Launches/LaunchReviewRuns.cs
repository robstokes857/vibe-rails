using VibeRails.DTOs;
using VibeRails.Launches;
using VibeRails.Services.Board;

namespace VibeRails.Services.Launches;

/// <summary>Adapts the new run ledger for existing review presentation without reading legacy Jobs.</summary>
internal static class LaunchReviewRuns
{
    internal static async Task<IReadOnlyList<BoardAgentRun>> ReadAsync(ILaunchStore? store, string project, string card,
        int offset, CancellationToken ct, string? runId = null, string? sessionId = null)
    {
        if (store is null) return [];
        return (await store.GetBoardRunsAsync(LaunchService.ProjectKey(project), card, offset, runId, sessionId, ct))
            .Where(r => r.Snapshot.Preset is not null && (r.Snapshot.Preset.Purpose == "code_review" || r.Snapshot.Context.SessionOrigin is "code_review" or "lane_review"))
            .Select(r => new BoardAgentRun(r.Id, r.Snapshot.Preset!.Name,
                r.Status == "queued" ? JobRunStatus.Queued : r.Status is "running" or "claimed" or "uncertain" ? JobRunStatus.Running
                : r.Status == "succeeded" ? JobRunStatus.Succeeded : r.Status == "cancelled" ? JobRunStatus.Cancelled : JobRunStatus.Failed,
                r.QueuedUtc, r.SessionId, r.SessionId, r.Error, "code_review",
                LlmParser.ToWireName(r.Snapshot.Preset.Cli), r.Snapshot.Preset.Name)).ToArray();
    }
}
