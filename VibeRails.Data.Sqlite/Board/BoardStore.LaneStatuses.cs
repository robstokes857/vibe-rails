using System.Text.Json;
using VibeRails.DTOs;

namespace VibeRails.Services.Board;

public sealed partial class BoardStore
{
    public async Task<IReadOnlyList<BoardLaneAutomationStatus>> GetLaneAutomationStatusesAsync(string projectPath,
        string cardId, CancellationToken cancellationToken = default)
    {
        var card = await FindCardAsync(projectPath, cardId, cancellationToken);
        if (card is null) return [];
        var rows = new List<BoardLaneAutomationStatus>();
        await using (var connection = await OpenAsync(cancellationToken))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                WITH pending AS ({PendingLaneEntriesSql}), entries AS (
                    SELECT p.EventKey, p.JobId, p.ColumnId, p.DueUnixMs, 'Waiting' AS Status,
                        '' AS Reason, NULL AS RunId, 0 AS priority
                    FROM pending p WHERE p.CardId = $card
                    UNION ALL
                    SELECT d.EventKey, d.JobId, d.ColumnId, d.DueUnixMs, d.Status, d.Reason, d.RunId, 1
                    FROM BoardLaneAutomationDispatch d WHERE d.CardId = $card
                        AND NOT EXISTS (SELECT 1 FROM pending p WHERE p.EventKey = d.EventKey AND p.JobId = d.JobId)
                )
                SELECT EventKey, JobId, ColumnId, DueUnixMs, Status, Reason, RunId FROM entries
                WHERE priority = 0 OR EventKey IN (
                    SELECT s.EventKey FROM BoardLaneWorkflowSteps s JOIN BoardLaneWorkflows w ON w.Id = s.WorkflowId
                    WHERE w.CardId = $card AND w.Current = 1)
                    OR EventKey IN (SELECT EventKey FROM BoardLaneAutomationDispatch WHERE CardId = $card ORDER BY DueUnixMs DESC LIMIT 100)
                ORDER BY priority, DueUnixMs DESC, EventKey;
                """;
            command.Parameters.AddWithValue("$card", card.Id);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                rows.Add(new(reader.GetString(0), reader.GetInt64(1), reader.GetString(2),
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)).UtcDateTime, "", reader.GetString(4),
                    reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6)));
        }
        if (rows.Count == 0) return rows;
        var definitions = (await DescribeLaneAutomationsAsync(projectPath, rows.Select(r => r.JobId).Distinct().ToList(), cancellationToken))
            .ToDictionary(d => d.JobId);
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            var definition = definitions[row.JobId];
            var reason = row.Status != "Waiting" ? row.Reason
                : row.DueUtc > DateTime.UtcNow ? "Waiting for the 60-second lane settling period."
                : definition.InProject && definition.ActiveRunId is not null ? $"Automation is busy with run {definition.ActiveRunId}; waiting for its turn."
                : "Waiting for the scheduler; a VibeRails root backend must be open.";
            rows[index] = row with { Name = definition.InProject ? definition.Name ?? $"Automation #{row.JobId}" : $"Automation #{row.JobId}",
                Purpose = definition.InProject ? definition.Purpose : "work", RequiresVerdict = definition.WorkerName is not null, Reason = reason };
        }

        // Reconcile by immutable trigger, even before Board acknowledgement, after a crash, or
        // when a move cancelled the pending entry concurrently with the independent run commit.
        await using var state = await OpenStateAsync(cancellationToken);
        if (!await _stateFeatures.HasTableAsync(state, "JobRuns", cancellationToken))
        {
            await ApplyWorkflowStatusesAsync(projectPath, card, rows, cancellationToken);
            return rows;
        }
        var keys = rows.Select(row => $"board-lane:{card.Key}:{row.ColumnId}:{row.EventKey}").ToList();
        var hasActions = await _stateFeatures.HasTableAsync(state, "JobRunActions", cancellationToken);
        var worker = hasActions ? "EXISTS (SELECT 1 FROM JobRunActions a WHERE a.RunId = JobRuns.Id AND a.Kind = 0)" : "1";
        var purpose = _stateFeatures.HasColumn(state, "JobRuns", "Purpose") ? "Purpose" : "'work'";
        await using var runs = state.CreateCommand();
        runs.CommandText = $"""
            SELECT TriggerKey, Id, JobName, Status, ErrorMessage, {purpose}, {worker} FROM JobRuns
            WHERE ProjectPath = $project{ProjectPathCollation} AND TriggerKind = $kind
                AND TriggerKey IN (SELECT value FROM json_each($keys));
            """;
        runs.Parameters.AddWithValue("$project", NormalizeProjectPath(projectPath));
        runs.Parameters.AddWithValue("$kind", (int)JobTriggerKind.BoardLane);
        runs.Parameters.AddWithValue("$keys", JsonSerializer.Serialize(keys, StorageJsonSerializerContext.Default.ListString));
        await using var runReader = await runs.ExecuteReaderAsync(cancellationToken);
        while (await runReader.ReadAsync(cancellationToken))
        {
            var index = keys.IndexOf(runReader.GetString(0));
            var status = (JobRunStatus)runReader.GetInt32(3);
            rows[index] = rows[index] with { RunId = runReader.GetString(1), Name = runReader.GetString(2), Status = status.ToString(),
                Reason = runReader.IsDBNull(4) ? $"Automation run {status.ToString().ToLowerInvariant()}." : runReader.GetString(4), Purpose = runReader.GetString(5), RequiresVerdict = runReader.GetBoolean(6) };
        }
        await runReader.DisposeAsync();
        if (hasActions) await ApplyDeterministicRetriesAsync(state, card, rows, cancellationToken);
        await ApplyWorkflowStatusesAsync(projectPath, card, rows, cancellationToken);
        return rows;
    }
}
