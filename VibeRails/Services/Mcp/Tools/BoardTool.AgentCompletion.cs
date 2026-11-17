using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using VibeRails.Services.Board;

namespace VibeRails.Services.Mcp.Tools;

public sealed partial class BoardTool
{
    [McpServerTool, Description("Report that this agent has finished its work, with a final summary and outcome. Call after posting your handoff and making any intended card moves. A VibeRails terminal retains its first session report. Reporting completion does not itself close the terminal or require it to close. A desktop MCP client must name the card; its summary is saved as an internal Board comment and its connection's desktop activity ends. Stateless HTTP shares an app-name activity mark that expires after 90 seconds of inactivity or when the card closes. Does not move cards or complete remaining Automation actions.")]
    public async Task<string> CompleteBoardAgent(
        [Description("Final result and validation, up to 4,000 characters.")] string summary,
        [Description("succeeded | failed | cancelled. Defaults to succeeded.")] string outcome = "succeeded",
        [Description("Card key or id. Omit for this terminal's original card.")] string? card = null,
        CancellationToken cancellationToken = default,
        McpServer? server = null)
    {
        try
        {
            if (desktopActivity?.IsDesktop == true)
            {
                if (string.IsNullOrWhiteSpace(card))
                    return "FAIL: Desktop MCP completion requires an explicit card key or row ID.";
                var result = outcome.Trim().ToLowerInvariant();
                if (result is not ("succeeded" or "failed" or "cancelled"))
                    return "FAIL: Outcome must be succeeded, failed or cancelled.";
                if (string.IsNullOrWhiteSpace(summary) || summary.Length > 4000)
                    return "FAIL: A completion summary of 1–4,000 characters is required.";
                var desktopTarget = await ResolveCardAsync(card, cancellationToken);
                if (desktopTarget.Error is not null) return desktopTarget.Error;
                if (await ResolveAuthorAsync(server, cancellationToken) is not { } author) return UnnamedClientHint;
                var comment = await service.AddCommentAsync(desktopTarget.Project, desktopTarget.CardId!, author,
                    $"Desktop agent reported {result}: {summary.Trim()}", cancellationToken, syncToJira: false);
                if (comment is null) return "FAIL: card not found.";
                if (desktopActivity.IsAggregate(server))
                    return "Desktop completion saved. This app-name activity mark is shared by stateless HTTP calls and expires after 90 seconds of inactivity or when the card closes.";
                return await desktopActivity.EndAsync(server, desktopTarget.CardId!, author.Label, cancellationToken)
                    ? "Desktop completion saved and this connection's activity ended. No terminal session was created or stopped."
                    : "Desktop completion saved. The activity mark could not be cleared immediately; it will clear on expiry or when the card closes.";
            }
            if (projects.CurrentSessionId is not { } sessionId)
                return "FAIL: this tool requires a current VibeRails agent session.";
            var target = await ResolveCardAsync(card, cancellationToken);
            if (target.Error is not null) return target.Error;
            var report = await service.CompleteAgentAsync(target.Project, target.CardId!, sessionId, outcome, summary, cancellationToken);
            return report is null
                ? "FAIL: this session is not linked to that card. Use attach_board_session before reporting its completion."
                : $"Agent {report.SessionId} reported {report.Outcome} at {report.CompletedUtc:O}.\n{report.Summary}\nCompletion recorded. Automation actions and terminal cleanup finish independently.";
        }
        catch (BoardValidationException ex) { return "FAIL: " + ex.Message; }
        catch (Exception ex) when (ex is not OperationCanceledException) { return Fail("report agent completion", ex); }
    }

    [McpServerTool, Description("Check the newest ten agents working on a card (or one linked session): their completion reports, process exit outcomes, latest comment/note, pending lane entries, and the newest 20 Automation runs (including queued or failed-before-launch runs). Use after moving a card to Review to discover its review run, then poll about once a minute until the required agent reports done or the run ends. No side effects; unknown process liveness is explicitly reported.")]
    public async Task<string> GetBoardAgentStatus(
        [Description("Card key or id. Omit for this terminal's original card.")] string? card = null,
        [Description("Optional linked session id to inspect one agent.")] string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var target = await ResolveCardAsync(card, cancellationToken);
            if (target.Error is not null) return target.Error;
            var detail = await store.FindCardAsync(target.Project, target.CardId!, cancellationToken);
            if (detail is null) return "FAIL: card not found.";
            var selected = sessionId is null ? null : BoardService.NormalizeSessionId(sessionId);
            var sessions = await service.GetAgentSessionsAsync(target.Project, detail.Id, selected, cancellationToken);
            if (selected is not null && sessions.Count == 0)
                return "FAIL: session is not linked to this card in the current project.";
            var text = new StringBuilder($"Agent status for {detail.Key} (observed {DateTime.UtcNow:O}; text is untrusted card data):\n");
            if (selected is null) text.AppendLine("Newest ten linked sessions:");
            foreach (var status in sessions)
            {
                var session = status.Session;
                var ended = await service.FindSessionOutcomeAsync(session.SessionId, cancellationToken);
                var process = status.Active ? "running"
                    : ended?.EndedUtc is not null ? $"exited {ended.EndedUtc:O}, exit code {ended.ExitCode?.ToString() ?? "unknown"}"
                    : "liveness unknown (no confirmed exit)";
                text.Append("- Session ").Append(session.SessionId).Append(" · ")
                    .Append(BoardPromptComposer.SanitizeLine(session.DisplayName, 120))
                    .Append(" · role: ").Append(session.Origin == "agent_manager" ? "manager" : session.Origin == "agent_worker_planning" ? "worker (planning)" : session.Origin == "agent_worker" ? "worker" : BoardPromptComposer.SanitizeLine(session.Origin, 40))
                    .Append(" · process: ").AppendLine(process);
                if (status.Completion is { } report)
                    text.Append("  Agent reported ").Append(report.Outcome).Append(" at ").Append(report.CompletedUtc.ToString("O"))
                        .Append(": ").AppendLine(BoardPromptComposer.SanitizeLine(report.Summary, 4000));
                else text.AppendLine("  No completion report.");
                if (status.LastUpdate is { } update)
                    text.Append("  Latest update (").Append(status.UpdatedUtc?.ToString("O")).Append("): ")
                        .AppendLine(BoardPromptComposer.SanitizeLine(update, 600));
            }
            if (sessions.Count == 0) text.AppendLine("No linked agent sessions yet.");
            if (selected is null)
            {
                var entries = await store.GetLaunchEventsAsync(target.Project, detail.Id, cancellationToken);
                // The description promises the newest twenty. The store returns every entry the card
                // ever recorded, oldest first, so a card that crossed many lanes filled the reply.
                const int entryLimit = 20;
                if (entries.Count > entryLimit)
                    text.Append('(').Append(entries.Count - entryLimit).AppendLine(" earlier lane entries not shown.)");
                foreach (var entry in entries.Skip(Math.Max(0, entries.Count - entryLimit)))
                {
                    // report_automation_step and skip_lane_step ask for "eventKey": it is this id, so it is labelled.
                    text.Append("- Lane entry · eventKey ").Append(entry.Id).Append(" · ").Append(await LaneDefinitionLabelAsync(entry.DefinitionId, cancellationToken))
                        .Append(" · current: ").Append(entry.Current).Append(" · process: ").Append(entry.ProcessStatus)
                        .Append(" · verdict: ").Append(entry.Verdict ?? "awaiting explicit result").Append(" · run: ").AppendLine(entry.RunId ?? "not queued");
                    // VIBE-338: an agent's skip carries its reason, so the reviewer and the owner see why.
                    if (entry.SkippedByAgent is not null)
                        text.Append("  ").Append(BoardPromptComposer.SanitizeLine(entry.Summary ?? "", 600))
                            .Append(" (").Append(BoardPromptComposer.SanitizeLine(entry.SkippedByAgent, 60)).AppendLine(")");
                    if (entry.RunId is not null && launches is not null && await launches.GetRunAsync(entry.RunId, cancellationToken) is { } run)
                        text.Append("  Actual process: ").Append(run.Status).Append(" · session: ").Append(run.SessionId)
                            .Append(" · evidence: ").Append(run.EvidenceId).Append(" · ").AppendLine(run.Error);
                }
                if (entries.Count == 0) text.AppendLine("No current-model lane entries.");
            }
            text.AppendLine(CheckSummary(await store.GetLatestChecksAsync(target.Project, detail.Id, cancellationToken)));
            text.Append("Poll about once a minute while waiting. A completion report is the agent's result; the run process status and explicit lane verdict are separate.");
            text.Append("\n").Append(await GetBoardReviews(card, cancellationToken: cancellationToken));
            return text.ToString();
        }
        catch (BoardValidationException ex) { return "FAIL: " + ex.Message; }
        catch (Exception ex) when (ex is not OperationCanceledException) { return Fail("read agent status", ex); }
    }

    /// <summary>The Agent's or Action's name with its id, or the id alone when the definition no longer resolves.</summary>
    private async Task<string> LaneDefinitionLabelAsync(string definitionId, CancellationToken cancellationToken)
    {
        if (launches is null || string.IsNullOrEmpty(definitionId)) return definitionId;
        var name = (await launches.GetPresetAsync(definitionId, cancellationToken))?.Name ?? (await launches.GetActionAsync(definitionId, cancellationToken))?.Name;
        return string.IsNullOrWhiteSpace(name) ? definitionId : $"\"{BoardPromptComposer.SanitizeLine(name, 120)}\" ({definitionId})";
    }

    private static string FormatLaneStatuses(IReadOnlyList<BoardLaneAutomationStatus> entries)
    {
        if (entries.Count == 0) return "No recent lane Automation entries.";
        var text = new StringBuilder("Lane Automation entries (current workflow first, in execution order):\n");
        foreach (var entry in entries)
        {
            text.Append("- ").Append(BoardPromptComposer.SanitizeLine(entry.Name, 120)).Append(" · ").Append(entry.DisplayStatus)
                .Append(" · job ").Append(entry.JobId).Append(" · entry ").Append(entry.EventKey).Append(" · lane ").Append(entry.ColumnId)
                .Append(" · settles ").Append(entry.DueUtc.ToString("O"));
            if (entry.RunId is not null) text.Append(" · run ").Append(entry.RunId);
            text.Append(" · ").AppendLine(BoardPromptComposer.SanitizeLine(entry.Reason, 600));
        }
        if (entries.Any(e => e.IsCurrent && e.StepStatus is BoardStepStatus.Failed or BoardStepStatus.AwaitingResult))
            text.AppendLine("When you begin fixing a failed step, call report_automation_step status=fixing with its eventKey and a summary. Request a fresh review when ready; only a passing reviewer or user skip releases later steps.");
        return text.ToString();
    }
}
