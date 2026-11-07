using VibeRails.DTOs;
using VibeRails.Launches;
using VibeRails.Services.Board;
using VibeRails.Services.Terminal;
using VibeRails.Utils;

namespace VibeRails.Services.Launches;

/// <summary>Ordered Board entries coordinate single runs through separate, recoverable database commits.</summary>
public sealed class BoardLaunchWorkflow(IBoardStore board, ILaunchStore runs, LaunchService launches,
    BoardReviewService reviews)
{
    /// <summary>Current per-card lane progress, scoped to the host's project.</summary>
    public async Task<IReadOnlyList<LaunchLaneEntry>?> GetLaneActivityAsync(string project, string column, CancellationToken ct)
    {
        if (await board.GetLaunchLaneAsync(project, column, ct) is null) return null;
        List<LaunchLaneEntry> result = [];
        foreach (var entry in (await board.GetLaunchEventsAsync(project, ct: ct)).Where(e => e.ColumnId == column && e.Current))
        {
            var card = await board.FindCardAsync(project, entry.CardId, ct);
            if (card is null) continue;
            var run = entry.RunId is null ? null : await launches.GetRunAsync(project, entry.RunId, ct);
            var name = entry.DefinitionType == "preset" ? (await runs.GetPresetAsync(entry.DefinitionId, ct))?.Name
                : (await runs.GetActionAsync(entry.DefinitionId, ct))?.Name;
            result.Add(new(entry, string.IsNullOrEmpty(card.DisplayId) ? card.Key : card.DisplayId,
                name ?? "Unavailable definition", run));
        }
        return result;
    }

    /// <summary>Queues a manual definition run with this project's saved card context and links.</summary>
    public async Task<LaunchRun?> EnqueueCardAsync(string project, string cardId, LaunchRequest request, CancellationToken ct)
    {
        var card = await board.FindCardAsync(project, cardId, ct);
        if (card is null) return null;
        var preset = request.DefinitionType == "preset" ? await runs.GetPresetAsync(request.DefinitionId, ct) : null;
        var levels = await ContextLevels.ResolveForCardAsync(board, project, card.BoardId, card.Type,
            ContextLevels.NudgeEnabled(Config.LoadFresh(), false), ct);
        var prompt = preset is null ? null : BoardPromptComposer.ComposeAutomationPrompt(card.Key, preset.InitialPrompt,
            preset.Purpose, card.WorkflowMode == "agent", ContextLevels.Format(levels));
        return await launches.EnqueueAsync(request.DefinitionType, request.DefinitionId,
            new(project, project, "manual", "board-manual:" + card.Id + ":" + (request.OccurrenceId ?? Guid.NewGuid().ToString("N")),
                BoardId: card.BoardId, CardId: card.Id, AuthorizeBoardTools: true, ContextIncluded: true,
                ComposedPrompt: prompt, SessionOrigin: "automation",
                Selection: preset is null ? null : $"preset:{preset.Id}:{LlmParser.ToWireName(preset.Cli).ToLowerInvariant()}"), ct, preset?.Revision);
    }

    public async Task<BoardLaunchLane?> SaveAsync(string project, string column, BoardLaunchLane lane, CancellationToken ct)
    {
        if (lane.Selections is null || lane.Selections.Any(s => s is null))
            throw new BoardValidationException("Lane selections are required.");
        var catalog = await launches.CatalogAsync(project, ct);
        foreach (var selection in lane.Selections)
        {
            var item = catalog.Items.SingleOrDefault(i => i.Id == selection.DefinitionId && i.DefinitionType == selection.DefinitionType);
            if (item?.CanRunAutomatically != true)
                throw new BoardValidationException(item?.IneligibleReason ?? "Select an enabled Agent or Action; Quick Launches cannot run on a lane.");
            if (selection.Gate == "review" && item.DefinitionType != "preset")
                throw new BoardValidationException("A review gate requires an Agent.");
        }
        return await board.SaveLaunchLaneAsync(project, column, lane, ct);
    }

    public async Task TickAsync(CancellationToken ct)
    {
        var entries = await board.GetLaunchEventsAsync(ct: ct);
        foreach (var entry in entries.Where(e => e.RunId is not null && !IsSettled(e)))
        {
            var run = await runs.GetRunAsync(entry.RunId!, ct);
            if (run is null) continue;
            if ((!entry.Current || entry.SkipRequested) && !run.IsTerminal)
            {
                await runs.CancelAsync(run.Id, run.ProjectPath, ct);
                run = (await runs.GetRunAsync(run.Id, ct))!;
            }
            // Every 2 s tick reaches here; write board.db only when the process status moved (VIBE-296).
            if (entry.ProcessStatus != run.Status)
                await board.UpdateLaunchEventAsync(entry.Id, run.Id, run.Status, ct);
            if (entry.Current && entry.Verdict is null && run.IsTerminal && run.Snapshot.Action is not null)
                await board.ReportLaunchEventAsync(entry.ProjectPath, entry.CardId, entry.Id,
                    run.Status == "succeeded" ? "passed" : "failed", run.Error ?? "Action completed: " + run.Status, run.SessionId, null, ct: ct, expectedRunId: run.Id);
        }
        entries = await board.GetLaunchEventsAsync(ct: ct);
        foreach (var entry in entries.Where(e => e.Current && e.RunId is null && !e.SkipRequested && e.DueUtc <= DateTime.UtcNow))
        {
            if (entries.Any(previous => previous.Current && previous.CardId == entry.CardId && previous.Position < entry.Position && !ReleasesNext(previous))) continue;
            var card = await board.FindCardAsync(entry.ProjectPath, entry.CardId, ct);
            if (card is null || card.ColumnId != entry.ColumnId) continue;
            try
            {
                LaunchPreset? preset = entry.DefinitionType == "preset" ? await runs.GetPresetAsync(entry.DefinitionId, ct) : null;
                var levels = await ContextLevels.ResolveForCardAsync(board, entry.ProjectPath, entry.BoardId, card.Type,
                    ContextLevels.NudgeEnabled(Config.LoadFresh(), false), ct);
                var prompt = preset is null ? null : BoardPromptComposer.ComposeAutomationPrompt(card.Key, preset.InitialPrompt,
                    entry.Gate == "review" ? "code_review" : preset.Purpose, card.WorkflowMode == "agent", ContextLevels.Format(levels));
                var run = await launches.EnqueueAsync(entry.DefinitionType, entry.DefinitionId,
                    new(entry.ProjectPath, entry.ProjectPath, "board-lane", "board-lane:" + entry.Id,
                        BoardId: entry.BoardId, CardId: entry.CardId, LaneEntryId: entry.Id, AuthorizeBoardTools: true,
                        ContextIncluded: true, ComposedPrompt: prompt, SessionOrigin: entry.Gate == "review" ? "lane_review" : "automation",
                        Selection: preset is null ? null : $"preset:{preset.Id}:{LlmParser.ToWireName(preset.Cli).ToLowerInvariant()}"), ct, preset?.Revision);
                // A crash here is recovered by the same occurrence key on the next tick.
                await board.AcknowledgeLaunchEventAsync(entry.Id, run.Id, ct);
            }
            catch (Exception ex) when (ex is LaunchValidationException or LaunchConflictException or Jobs.AutomationScriptValidationException)
            {
                Serilog.Log.Warning("[Launches] Lane entry {Entry} is ineligible: {Reason}", entry.Id, ex.Message);
            }
        }
    }

    /// <summary>
    /// An entry whose recorded run already ended needs no more ticking: its terminal status is stored,
    /// and an Action's verdict is reported (a preset reports its own). A retry replaces the run id and
    /// resets the status to queued, which makes the entry live again.
    /// </summary>
    internal static bool IsSettled(BoardLaunchEvent entry) =>
        entry.ProcessStatus is "succeeded" or "failed" or "cancelled" or "timed-out" or "interrupted"
        && (entry.Verdict is not null || entry.DefinitionType != "action" || !entry.Current);

    public static bool ReleasesNext(BoardLaunchEvent entry)
    {
        if (entry.SkipRequested && entry.RunId is null) return true;
        if (entry.ProcessStatus is "queued" or "claimed" or "running" or "uncertain") return false;
        return entry.SkipRequested || entry.ProcessStatus == "succeeded" && (entry.Verdict == "passed" || entry.Gate == "process");
    }

    public async Task<bool> TryReportAsync(string project, string cardId, string workspace, BoardAuthor author,
        string status, string summary, string? eventKey, string? reviewId, CancellationToken ct)
    {
        var entries = await board.GetLaunchEventsAsync(project, cardId, ct);
        BoardLaunchEvent? entry = null;
        LaunchRun? run = null;
        foreach (var candidate in entries.Where(e => e.Current && (eventKey is null || e.Id == eventKey)))
        {
            var current = candidate.RunId is null ? null : await runs.GetRunAsync(candidate.RunId, ct);
            if (eventKey is not null || current?.SessionId == author.SessionId && author.SessionId is not null)
            { entry = candidate; run = current; break; }
        }
        if (entry is null) return false;
        if (author.SessionId is null || !(await board.GetAgentSessionsAsync(project, cardId, author.SessionId, ct)).Any())
            throw new BoardValidationException("Only a linked terminal session can report this lane entry.");
        status = status switch { "pass" => "passed", "fail" => "failed", _ => status };
        if (status != "fixing" && (run is null || run.SessionId != author.SessionId || run.IsTerminal))
            throw new BoardValidationException("Only this entry's live run may report its result.");
        if (string.IsNullOrWhiteSpace(summary) || summary.Length > 4000)
            throw new BoardValidationException("A summary of 1–4,000 characters is required.");
        if (entry.Gate == "review" && status == "passed")
        {
            if (reviewId is null) throw new BoardValidationException("Save the review and supply reviewId before passing the entry.");
            var report = await reviews.ReportAsync(project, cardId, reviewId, true, workspace, ct);
            if (report is null || report.SessionId != author.SessionId || report.ReportedUtc is null
                || report.Result == "Incomplete" || !report.Freshness.StartsWith("Current", StringComparison.Ordinal))
                throw new BoardValidationException("A pass requires your complete saved review with current captured inputs.");
        }
        if (!await board.ReportLaunchEventAsync(project, cardId, entry.Id, status, summary, author.SessionId, reviewId, ct: ct, expectedRunId: run?.Id))
            throw new BoardConflictException("The lane entry changed or already completed.");
        if (run?.ClaimToken is { } token && status != "fixing")
            await runs.RecordVerdictAsync(new(run.Id, token), status, reviewId, ct);
        return true;
    }
}
