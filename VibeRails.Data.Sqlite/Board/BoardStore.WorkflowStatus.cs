namespace VibeRails.Services.Board;

public sealed partial class BoardStore
{
    private static void ApplyWorkflowStatuses(LaneStatusCard card,
        List<BoardLaneAutomationStatus> rows, WorkflowStatusData data)
    {
        var metadata = data.Metadata;
        var reports = data.Reports;
        var selected = data.Selections.GetValueOrDefault(card.ColumnId) ?? [];
        var legacyDue = rows.Where(r => r.ColumnId == card.ColumnId).Select(r => (DateTime?)r.DueUtc).Max();
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var key = row.EventKey + ":" + row.JobId;
            var hasMetadata = metadata.TryGetValue(key, out var meta);
            var current = hasMetadata ? meta.Current : row.ColumnId == card.ColumnId && row.DueUtc == legacyDue && selected.Contains(row.JobId);
            var workflow = hasMetadata ? meta.Workflow : $"legacy:{card.Id}:{row.DueUtc.Ticks}";
            var position = hasMetadata ? meta.Position : selected.IndexOf(row.JobId);
            var step = row.Status == BoardStepStatus.Succeeded
                ? row.RequiresVerdict ? BoardStepStatus.AwaitingResult : BoardStepStatus.Passed
                : row.Status == BoardStepStatus.Running && row.Purpose == "code_review" ? BoardStepStatus.Reviewing : row.Status;
            var reason = step == BoardStepStatus.AwaitingResult ? "The agent finished without an explicit pass/fail decision. Later steps remain waiting." : row.Reason;
            var hasReport = reports.TryGetValue(key, out var report);
            var newerRun = hasReport && row.RunId is not null && report.Run is not null
                && data.RunOrder.GetValueOrDefault(row.RunId) > data.RunOrder.GetValueOrDefault(report.Run);
            // A newer exact-entry retry needs its own verdict. Reports on the old attempt
            // cannot hide its queued/running state or pass it. A user skip still wins.
            if (hasReport
                && !(report.Status != BoardStepStatus.Skipped && newerRun
                    && !(report.Status == BoardStepStatus.Passed
                        && data.RunStatuses.GetValueOrDefault(report.Run!) == BoardStepStatus.Succeeded))
                && !(report.Status == BoardStepStatus.Fixing && !row.RequiresVerdict
                    && row.RunId is not null && report.Run != row.RunId))
            {
                var process = row.Status;
                var skipNewerRun = report.Status == BoardStepStatus.Skipped && newerRun;
                if (!skipNewerRun && report.Run is not null && report.Run != row.RunId)
                    process = data.RunStatuses.GetValueOrDefault(report.Run, BoardStepStatus.Unknown);
                if (!skipNewerRun && report.Run is not null) row = row with { RunId = report.Run, Status = process };
                step = report.Status switch
                {
                    BoardStepStatus.Skipped => row.RunId is not null && row.Status is BoardStepStatus.Queued or BoardStepStatus.Running
                        ? BoardStepStatus.Stopping : BoardStepStatus.Skipped,
                    BoardStepStatus.Passed => process == BoardStepStatus.Succeeded ? BoardStepStatus.Passed
                        : process is BoardStepStatus.Queued or BoardStepStatus.Running
                            ? row.Purpose == "code_review" ? BoardStepStatus.Reviewing : BoardStepStatus.Running
                            : BoardStepStatus.Failed,
                    BoardStepStatus.Reviewing => process == BoardStepStatus.Succeeded ? BoardStepStatus.AwaitingResult
                        : process is BoardStepStatus.Queued or BoardStepStatus.Running
                            ? BoardStepStatus.Reviewing : BoardStepStatus.Failed,
                    _ => report.Status
                };
                reason = report.Status == BoardStepStatus.Passed && process is BoardStepStatus.Queued or BoardStepStatus.Running
                    ? "Pass reported; waiting for the Automation to finish. " + report.Summary : report.Summary;
                if (step == BoardStepStatus.AwaitingResult) reason = "The agent finished without an explicit pass/fail decision. Later steps remain waiting.";
                if (report.Status is BoardStepStatus.Passed or BoardStepStatus.Reviewing && step == BoardStepStatus.Failed)
                    reason = $"Automation run {process.ToLowerInvariant()}; a new attempt or user skip is required. {report.Summary}";
            }
            rows[i] = row with { WorkflowId = workflow, Position = position, IsCurrent = current, StepStatus = step,
                CanSkip = current && step is not (BoardStepStatus.Passed or BoardStepStatus.Skipped or BoardStepStatus.Stopping), Reason = reason };
        }
        foreach (var row in rows.Where(r => r.IsCurrent && r.Status == BoardStepStatus.Waiting).ToList())
        {
            var blocker = rows.Where(r => r.WorkflowId == row.WorkflowId && r.Position < row.Position)
                .OrderBy(r => r.Position).FirstOrDefault(r => r.StepStatus is not (BoardStepStatus.Passed or BoardStepStatus.Skipped));
            if (blocker is not null)
            {
                var index = rows.IndexOf(row);
                rows[index] = row with { Reason = $"Waiting for {blocker.Name}: {blocker.StepStatus}." };
            }
        }
        rows.Sort((a, b) =>
        {
            var order = b.IsCurrent.CompareTo(a.IsCurrent);
            if (order != 0) return order;
            order = b.DueUtc.CompareTo(a.DueUtc);
            if (order != 0) return order;
            order = StringComparer.Ordinal.Compare(a.WorkflowId, b.WorkflowId);
            return order != 0 ? order : a.Position.CompareTo(b.Position);
        });
    }

    public async Task<string?> GetLaneAutomationBlockReasonAsync(BoardLaneAutomationEvent entry, CancellationToken cancellationToken = default)
    {
        var rows = await GetLaneAutomationStatusesAsync(entry.ProjectPath, entry.CardId, cancellationToken);
        var target = rows.FirstOrDefault(r => r.EventKey == entry.EventKey && r.JobId == entry.JobId);
        if (target is null || !target.IsCurrent) return "Lane workflow is no longer current.";
        var previous = rows.Where(r => r.WorkflowId == target.WorkflowId && r.Position < target.Position)
            .OrderBy(r => r.Position).FirstOrDefault(r => r.StepStatus is not (BoardStepStatus.Passed or BoardStepStatus.Skipped));
        return previous is null ? null : $"Waiting for {previous.Name}: {previous.StepStatus}.";
    }

    public async Task<IReadOnlyList<BoardLaneWorkflow>> GetLaneWorkflowsAsync(string projectPath, string columnId, CancellationToken cancellationToken = default)
    {
        var project = NormalizeProjectPath(projectPath);
        var cards = new List<LaneStatusCard>();
        await using var connection = await OpenAsync(cancellationToken);
        await using (var query = connection.CreateCommand())
        {
            query.CommandText = $"""
                SELECT c.Id, {CardKeySql}, c.ColumnId, COALESCE(c.DisplayId, {CardKeySql}) || ' · ' || c.Title
                FROM BoardCards c {CardPrefixJoinSql}
                WHERE c.ColumnId = $lane AND c.ProjectPath = $project{ProjectPathCollation} AND c.DeletedUTC IS NULL
                  AND (EXISTS (SELECT 1 FROM BoardLaneWorkflows w WHERE w.CardId = c.Id AND w.Current = 1)
                    OR EXISTS (SELECT 1 FROM ({PendingLaneEntriesSql}) p WHERE p.CardId = c.Id))
                ORDER BY c.Position LIMIT 100;
                """;
            query.Parameters.AddWithValue("$lane", columnId);
            query.Parameters.AddWithValue("$project", project);
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                cards.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        }
        var rows = await ReadLaneStatusesAsync(connection, project, cards, cancellationToken);
        return cards.Select(card => new BoardLaneWorkflow(card.Id, card.Label,
            rows[card.Id].Where(r => r.IsCurrent).ToList())).ToList();
    }
}
