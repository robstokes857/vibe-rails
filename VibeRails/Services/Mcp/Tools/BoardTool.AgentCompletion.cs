using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using VibeRails.Services.Board;

namespace VibeRails.Services.Mcp.Tools;

public sealed partial class BoardTool
{
    [McpServerTool, Description("Report that this agent has finished its work, with a final summary and outcome. Call after posting your handoff and moving the card. A VibeRails terminal retains its first session report; then call end_agent_session LAST to close its PTY. A desktop MCP client must name the card; its summary is saved as an internal Board comment and its connection's desktop activity ends. Stateless HTTP shares an app-name activity mark that expires after 90 seconds of inactivity or when the card closes. Does not move cards or complete remaining Automation actions.")]
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
                : $"Agent {report.SessionId} reported {report.Outcome} at {report.CompletedUtc:O}.\n{report.Summary}\nYou may now exit. Automation actions and terminal cleanup finish independently.";
        }
        catch (BoardValidationException ex) { return "FAIL: " + ex.Message; }
        catch (Exception ex) when (ex is not OperationCanceledException) { return Fail("report agent completion", ex); }
    }

    [McpServerTool, Description("Check the newest ten agents working on a card (or one linked session): their completion reports, process exit outcomes, latest comment/note, pending lane entries, and the newest 20 Automation runs (including queued or failed-before-launch runs). Use after moving a card to Review to discover its review run, then poll every 10 seconds until the required agent reports done or the run ends. No side effects; unknown process liveness is explicitly reported.")]
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
                    .Append(BoardPromptComposer.SanitizeLine(session.DisplayName, 120)).Append(" · process: ").AppendLine(process);
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
                var entries = await store.GetLaneAutomationStatusesAsync(target.Project, detail.Id, cancellationToken);
                text.AppendLine(FormatLaneStatuses(entries));
                var runs = await store.GetAgentRunsAsync(target.Project, detail.Id, cancellationToken);
                foreach (var run in runs)
                {
                    text.Append("- Run ").Append(run.Id).Append(" · ").Append(BoardPromptComposer.SanitizeLine(run.Name, 120))
                        .Append(" · ").Append(run.Status).Append(" · queued ").Append(run.QueuedUtc.ToString("O"));
                    if (run.SessionId is not null) text.Append(" · terminal session ").Append(run.SessionId);
                    if (run.WorkerSessionId is not null) text.Append(" · agent session ").Append(run.WorkerSessionId);
                    if (run.Error is not null) text.Append(" · ").Append(BoardPromptComposer.SanitizeLine(run.Error, 600));
                    text.AppendLine();
                }
                if (runs.Count == 0 && entries.Count == 0) text.AppendLine("No pending entries or recent card Automation runs.");
            }
            text.Append("Poll again in about 10 seconds while waiting. A completion report is the agent's result; only a terminal run status confirms all Automation actions finished.");
            text.Append("\n").Append(await GetBoardReviews(card, cancellationToken: cancellationToken));
            return text.ToString();
        }
        catch (BoardValidationException ex) { return "FAIL: " + ex.Message; }
        catch (Exception ex) when (ex is not OperationCanceledException) { return Fail("read agent status", ex); }
    }

    private static string FormatLaneStatuses(IReadOnlyList<BoardLaneAutomationStatus> entries)
    {
        if (entries.Count == 0) return "No recent lane Automation entries.";
        var text = new StringBuilder("Lane Automation entries (current workflow first, in execution order):\n");
        foreach (var entry in entries)
        {
            text.Append("- ").Append(BoardPromptComposer.SanitizeLine(entry.Name, 120)).Append(" · ").Append(entry.StepStatus ?? entry.Status)
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
