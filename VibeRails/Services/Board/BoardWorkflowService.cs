namespace VibeRails.Services.Board;

/// <summary>Explicit agent progress and decisions for the current ordered lane workflow.</summary>
public sealed class BoardWorkflowService(IBoardStore store, IBoardService board)
{
    /// <summary>Record a scoped decision; a review pass must cite the caller's current canonical evidence.</summary>
    public async Task ReportAsync(string project, string cardId, string workspace, BoardAuthor author,
        string status, string summary, string? eventKey, string? reviewId, CancellationToken ct)
    {
        if (eventKey?.Length > 100 || reviewId?.Length > 100) throw new BoardValidationException("Invalid step or review identity.");
        var session = author.SessionId ?? throw new BoardValidationException("A current VibeRails agent session is required.");
        status = status.Trim().ToLowerInvariant() switch
        {
            "reviewing" => "Reviewing", "fixing" => "Fixing", "passed" or "pass" => "Passed",
            "failed" or "fail" => "Failed", _ => throw new BoardValidationException("Use reviewing, fixing, passed or failed. Only the user can skip a step.")
        };
        if (string.IsNullOrWhiteSpace(summary) || summary.Length > 4000)
            throw new BoardValidationException("A summary of 1–4,000 characters is required.");
        if (!(await store.GetAgentSessionsAsync(project, cardId, session, ct)).Any())
            throw new BoardValidationException("This session is not linked to the card.");
        var run = await store.FindLaneStepRunAsync(project, cardId, session, ct);
        var entries = (await store.GetLaneAutomationStatusesAsync(project, cardId, ct)).Where(e => e.IsCurrent).ToList();
        var entry = eventKey is null ? entries.SingleOrDefault(e => e.RunId == run?.Id && run is not null)
            : entries.SingleOrDefault(e => e.EventKey == eventKey);
        if (entry is null) throw new BoardValidationException("Choose the current workflow entry from get_board_agent_status. For a new review run, pass its original step's eventKey.");
        if (entry.StepStatus is "Passed" or "Skipped" or "Stopping")
            throw new BoardConflictException("This workflow step is already complete or being skipped.");
        if (status == "Fixing")
        {
            if (entry.StepStatus is not ("Failed" or "Fixing" or "Awaiting result"))
                throw new BoardConflictException("Only a failed or incomplete step can be marked fixing.");
        }
        else
        {
            if (run is null || !run.Active || run.JobId != entry.JobId || run.QueuedUtc < entry.DueUtc.AddSeconds(-60))
                throw new BoardValidationException("Only a current run of this Automation can report its decision.");
            if (run.TriggerKey.StartsWith("board-lane:", StringComparison.Ordinal) && !run.TriggerKey.EndsWith(":" + entry.EventKey, StringComparison.Ordinal))
                throw new BoardValidationException("This run belongs to a different lane entry.");
        }
        if (status == "Passed" && entry.Purpose == "code_review")
        {
            if (reviewId is null) throw new BoardValidationException("Save the code review and supply reviewId before reporting a pass.");
            var report = await new BoardReviewService(store, board).ReportAsync(project, cardId, reviewId, true, workspace, ct);
            if (report is null || report.SessionId != session || report.ReportedUtc is null
                || report.Result == "Incomplete" || !report.Freshness.StartsWith("Current", StringComparison.Ordinal))
                throw new BoardValidationException("A pass requires your saved, complete review with inputs that still match this checkout.");
        }
        if (!await store.ReportLaneStepAsync(project, cardId,
            new(entry.EventKey, entry.JobId, status, summary, status == "Fixing" ? entry.RunId : run!.Id, reviewId), author, ct))
            throw new BoardConflictException("The lane entry changed; refresh its status.");
    }
}
