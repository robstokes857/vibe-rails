using System.Text.Json;
using Microsoft.Data.Sqlite;
using VibeRails.DTOs;

namespace VibeRails.Services.Board;

public sealed partial class BoardStore
{
    private async Task ApplyWorkflowStatusesAsync(string project, BoardCardRecord card,
        List<BoardLaneAutomationStatus> rows, CancellationToken ct)
    {
        var metadata = new Dictionary<string, (string Workflow, int Position, bool Current)>();
        var reports = new Dictionary<string, (string Status, string Summary, string? Run)>();
        await using (var connection = await OpenAsync(ct))
        {
            await using var query = connection.CreateCommand();
            query.CommandText = """
                SELECT s.EventKey, s.JobId, w.Id, s.Position, w.Current AND w.ColumnId = $lane
                FROM BoardLaneWorkflows w JOIN BoardLaneWorkflowSteps s ON s.WorkflowId = w.Id
                WHERE w.CardId = $card AND s.EventKey IN (SELECT value FROM json_each($events));
                """;
            query.Parameters.AddWithValue("$card", card.Id);
            query.Parameters.AddWithValue("$lane", card.ColumnId);
            query.Parameters.AddWithValue("$events", JsonSerializer.Serialize(rows.Select(r => r.EventKey).Distinct().ToList(), StorageJsonSerializerContext.Default.ListString));
            await using (var reader = await query.ExecuteReaderAsync(ct))
                while (await reader.ReadAsync(ct))
                    metadata[reader.GetString(0) + ":" + reader.GetInt64(1)] =
                        (reader.GetString(2), reader.GetInt32(3), reader.GetBoolean(4));
            query.CommandText = """
                SELECT r.EventKey, r.JobId, r.Status, r.Summary, r.RunId FROM BoardLaneStepReports r
                WHERE r.EventKey IN (SELECT value FROM json_each($events))
                AND r.Id = (SELECT MAX(latest.Id) FROM BoardLaneStepReports latest WHERE latest.EventKey = r.EventKey AND latest.JobId = r.JobId);
                """;
            await using var reportReader = await query.ExecuteReaderAsync(ct);
            while (await reportReader.ReadAsync(ct))
                reports[reportReader.GetString(0) + ":" + reportReader.GetInt64(1)] =
                    (reportReader.GetString(2), reportReader.GetString(3), reportReader.IsDBNull(4) ? null : reportReader.GetString(4));
        }
        // Pending entries created before this additive schema keep working, without converting
        // history. SQLite's 'now' is stable across every queue trigger in the same statement.
        var selected = (await GetLaneAutomationAsync(project, card.ColumnId, ct))?.JobIds.ToList() ?? [];
        var legacyDue = rows.Where(r => r.ColumnId == card.ColumnId).Select(r => (DateTime?)r.DueUtc).Max();
        await using var state = await OpenStateAsync(ct);
        var hasRuns = await _stateFeatures.HasTableAsync(state, "JobRuns", ct);
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var key = row.EventKey + ":" + row.JobId;
            var hasMetadata = metadata.TryGetValue(key, out var meta);
            var current = hasMetadata ? meta.Current : row.ColumnId == card.ColumnId && row.DueUtc == legacyDue && selected.Contains(row.JobId);
            var workflow = hasMetadata ? meta.Workflow : $"legacy:{card.Id}:{row.DueUtc.Ticks}";
            var position = hasMetadata ? meta.Position : selected.IndexOf(row.JobId);
            var step = row.Status == "Succeeded" ? row.RequiresVerdict ? "Awaiting result" : "Passed"
                : row.Status == "Running" && row.Purpose == "code_review" ? "Reviewing" : row.Status;
            var reason = step == "Awaiting result" ? "The agent finished without an explicit pass/fail decision. Later steps remain waiting." : row.Reason;
            // Fixing describes the failed attempt. Once a deterministic retry is queued,
            // its own process outcome supersedes that progress note. A user skip still wins.
            if (reports.TryGetValue(key, out var report)
                && !(report.Status == "Fixing" && !row.RequiresVerdict
                    && row.RunId is not null && report.Run != row.RunId))
            {
                var process = row.Status;
                if (report.Run is not null && report.Run != row.RunId && hasRuns)
                {
                    await using var run = state.CreateCommand();
                    run.CommandText = $"SELECT Status FROM JobRuns WHERE Id = $run AND ProjectPath = $project{ProjectPathCollation};";
                    run.Parameters.AddWithValue("$run", report.Run);
                    run.Parameters.AddWithValue("$project", NormalizeProjectPath(project));
                    var value = await run.ExecuteScalarAsync(ct);
                    process = value is long number ? ((JobRunStatus)number).ToString() : "Unknown";
                }
                if (report.Run is not null) row = row with { RunId = report.Run, Status = process };
                step = report.Status switch
                {
                    "Skipped" => row.RunId is not null && row.Status is "Queued" or "Running" ? "Stopping" : "Skipped",
                    "Passed" => process == "Succeeded" ? "Passed" : process is "Queued" or "Running" ? row.Purpose == "code_review" ? "Reviewing" : "Running" : "Failed",
                    "Reviewing" => process == "Succeeded" ? "Awaiting result" : process is "Queued" or "Running" ? "Reviewing" : "Failed",
                    _ => report.Status
                };
                reason = report.Status == "Passed" && process is "Queued" or "Running"
                    ? "Pass reported; waiting for the Automation to finish. " + report.Summary : report.Summary;
                if (step == "Awaiting result") reason = "The agent finished without an explicit pass/fail decision. Later steps remain waiting.";
                if (report.Status is "Passed" or "Reviewing" && step == "Failed")
                    reason = $"Automation run {process.ToLowerInvariant()}; a new attempt or user skip is required. {report.Summary}";
            }
            rows[i] = row with { WorkflowId = workflow, Position = position, IsCurrent = current, StepStatus = step,
                CanSkip = current && step is not ("Passed" or "Skipped" or "Stopping"), Reason = reason };
        }
        foreach (var row in rows.Where(r => r.IsCurrent && r.Status == "Waiting").ToList())
        {
            var blocker = rows.Where(r => r.WorkflowId == row.WorkflowId && r.Position < row.Position)
                .OrderBy(r => r.Position).FirstOrDefault(r => r.StepStatus is not ("Passed" or "Skipped"));
            if (blocker is not null)
            {
                var index = rows.IndexOf(row);
                rows[index] = row with { Reason = $"Waiting for {blocker.Name}: {blocker.StepStatus}." };
            }
        }
        rows.Sort((a, b) => a.WorkflowId == b.WorkflowId ? a.Position.CompareTo(b.Position)
            : a.IsCurrent != b.IsCurrent ? a.IsCurrent ? -1 : 1 : b.DueUtc.CompareTo(a.DueUtc));
    }

    public async Task<string?> GetLaneAutomationBlockReasonAsync(BoardLaneAutomationEvent entry, CancellationToken cancellationToken = default)
    {
        var rows = await GetLaneAutomationStatusesAsync(entry.ProjectPath, entry.CardId, cancellationToken);
        var target = rows.FirstOrDefault(r => r.EventKey == entry.EventKey && r.JobId == entry.JobId);
        if (target is null || !target.IsCurrent) return "Lane workflow is no longer current.";
        var previous = rows.Where(r => r.WorkflowId == target.WorkflowId && r.Position < target.Position)
            .OrderBy(r => r.Position).FirstOrDefault(r => r.StepStatus is not ("Passed" or "Skipped"));
        return previous is null ? null : $"Waiting for {previous.Name}: {previous.StepStatus}.";
    }

    public async Task<IReadOnlyList<BoardLaneWorkflow>> GetLaneWorkflowsAsync(string projectPath, string columnId, CancellationToken cancellationToken = default)
    {
        var cards = new List<(string Id, string Label)>();
        await using (var connection = await OpenAsync(cancellationToken))
        {
            await using var query = connection.CreateCommand();
            query.CommandText = $"""
                SELECT c.Id, COALESCE(c.DisplayId, {CardKeySql}) || ' · ' || c.Title
                FROM BoardCards c {CardPrefixJoinSql}
                WHERE c.ColumnId = $lane AND c.ProjectPath = $project{ProjectPathCollation} AND c.DeletedUTC IS NULL
                  AND (EXISTS (SELECT 1 FROM BoardLaneWorkflows w WHERE w.CardId = c.Id AND w.Current = 1)
                    OR EXISTS (SELECT 1 FROM ({PendingLaneEntriesSql}) p WHERE p.CardId = c.Id))
                ORDER BY c.Position LIMIT 100;
                """;
            query.Parameters.AddWithValue("$lane", columnId);
            query.Parameters.AddWithValue("$project", NormalizeProjectPath(projectPath));
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) cards.Add((reader.GetString(0), reader.GetString(1)));
        }
        var result = new List<BoardLaneWorkflow>();
        foreach (var card in cards)
            result.Add(new(card.Id, card.Label, (await GetLaneAutomationStatusesAsync(projectPath, card.Id, cancellationToken)).Where(r => r.IsCurrent).ToList()));
        return result;
    }
}
