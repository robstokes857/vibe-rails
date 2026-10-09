using System.Text.Json;
using Microsoft.Data.Sqlite;
using VibeRails.DTOs;

namespace VibeRails.Services.Board;

public sealed partial class BoardStore
{
    private sealed record LaneStatusCard(string Id, string Key, string ColumnId, string Label);

    public async Task<IReadOnlyList<BoardLaneAutomationStatus>> GetLaneAutomationStatusesAsync(string projectPath,
        string cardId, CancellationToken cancellationToken = default)
    {
        var project = NormalizeProjectPath(projectPath);
        await using var board = await OpenAsync(cancellationToken);
        var card = await ReadCardAsync(board, null, project, cardId, cancellationToken);
        if (card is null) return [];
        var rows = await ReadLaneStatusesAsync(board, project,
            [new(card.Id, card.Key, card.ColumnId, "")], cancellationToken);
        return rows[card.Id];
    }

    // One read-local cache for the entire batch: each definition, workflow receipt and run is
    // loaded once, then shared by all cards. Nothing outlives the request or hides another root's
    // skip/run completion from scheduler checks.
    private async Task<Dictionary<string, List<BoardLaneAutomationStatus>>> ReadLaneStatusesAsync(
        SqliteConnection board, string project, IReadOnlyList<LaneStatusCard> cards, CancellationToken ct)
    {
        var result = cards.ToDictionary(c => c.Id, _ => new List<BoardLaneAutomationStatus>());
        if (cards.Count == 0) return result;
        var cardIds = JsonSerializer.Serialize(cards.Select(c => c.Id).ToList(), StorageJsonSerializerContext.Default.ListString);
        await using (var query = board.CreateCommand())
        {
            query.CommandText = $"""
                WITH pending AS ({PendingLaneEntriesSql}), history AS (
                    SELECT d.*, ROW_NUMBER() OVER (PARTITION BY CardId ORDER BY DueUnixMs DESC) AS recency
                    FROM BoardLaneAutomationDispatch d WHERE CardId IN (SELECT value FROM json_each($cards))
                ), entries AS (
                    SELECT p.CardId, p.EventKey, p.JobId, p.ColumnId, p.DueUnixMs, 'Waiting' AS Status,
                        '' AS Reason, NULL AS RunId, 0 AS priority
                    FROM pending p WHERE p.CardId IN (SELECT value FROM json_each($cards))
                    UNION ALL
                    SELECT d.CardId, d.EventKey, d.JobId, d.ColumnId, d.DueUnixMs, d.Status, d.Reason, d.RunId, 1
                    FROM history d WHERE NOT EXISTS (
                        SELECT 1 FROM pending p WHERE p.EventKey = d.EventKey AND p.JobId = d.JobId)
                    AND (d.recency <= 100 OR d.EventKey IN (
                        SELECT s.EventKey FROM BoardLaneWorkflowSteps s JOIN BoardLaneWorkflows w ON w.Id = s.WorkflowId
                        WHERE w.CardId = d.CardId AND w.Current = 1))
                )
                SELECT CardId, EventKey, JobId, ColumnId, DueUnixMs, Status, Reason, RunId FROM entries
                ORDER BY priority, DueUnixMs DESC, EventKey;
                """;
            query.Parameters.AddWithValue("$cards", cardIds);
            await using var reader = await query.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                result[reader.GetString(0)].Add(new(reader.GetString(1), reader.GetInt64(2), reader.GetString(3),
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4)).UtcDateTime, "", reader.GetString(5),
                    reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7)));
        }
        var allRows = result.Values.SelectMany(rows => rows).ToList();
        if (allRows.Count == 0) return result;
        await using var state = await OpenStateAsync(ct);
        var definitions = (await DescribeLaneAutomationsAsync(state, project,
            allRows.Select(r => r.JobId).Distinct().ToList(), ct)).ToDictionary(d => d.JobId);
        var byTrigger = new Dictionary<string, (List<BoardLaneAutomationStatus> Rows, int Index)>();
        var now = DateTime.UtcNow;
        foreach (var card in cards)
        {
            var rows = result[card.Id];
            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                var definition = definitions[row.JobId];
                var reason = row.Status != BoardStepStatus.Waiting ? row.Reason
                    : row.DueUtc > now ? "Waiting for the 60-second lane settling period."
                    : definition.InProject && definition.ActiveRunId is not null ? $"Automation is busy with run {definition.ActiveRunId}; waiting for its turn."
                    : "Waiting for the scheduler; a VibeRails root backend must be open.";
                rows[i] = row with { Name = definition.InProject ? definition.Name ?? $"Automation #{row.JobId}" : $"Automation #{row.JobId}",
                    Purpose = definition.InProject ? definition.Purpose : "work", RequiresVerdict = definition.WorkerName is not null, Reason = reason };
                byTrigger[$"board-lane:{card.Key}:{row.ColumnId}:{row.EventKey}"] = (rows, i);
            }
        }

        // Reconcile immutable trigger keys even before acknowledgement or after a racing move.
        var hasRuns = await _stateFeatures.HasTableAsync(state, "JobRuns", ct);
        if (hasRuns)
        {
            var hasActions = await _stateFeatures.HasTableAsync(state, "JobRunActions", ct);
            var worker = hasActions ? "EXISTS (SELECT 1 FROM JobRunActions a WHERE a.RunId = JobRuns.Id AND a.Kind = 0)" : "1";
            var purpose = _stateFeatures.HasColumn(state, "JobRuns", "Purpose") ? "Purpose" : "'work'";
            await using var runs = state.CreateCommand();
            runs.CommandText = $"""
                SELECT TriggerKey, Id, JobName, Status, ErrorMessage, {purpose}, {worker} FROM JobRuns
                WHERE ProjectPath = $project{ProjectPathCollation} AND TriggerKind = $kind
                    AND TriggerKey IN (SELECT value FROM json_each($keys));
                """;
            runs.Parameters.AddWithValue("$project", project);
            runs.Parameters.AddWithValue("$kind", (int)JobTriggerKind.BoardLane);
            runs.Parameters.AddWithValue("$keys", JsonSerializer.Serialize(byTrigger.Keys.ToList(), StorageJsonSerializerContext.Default.ListString));
            await using (var reader = await runs.ExecuteReaderAsync(ct))
                while (await reader.ReadAsync(ct))
                {
                    var (rows, index) = byTrigger[reader.GetString(0)];
                    var status = (JobRunStatus)reader.GetInt32(3);
                    rows[index] = rows[index] with { RunId = reader.GetString(1), Name = reader.GetString(2), Status = status.ToString(),
                        Reason = reader.IsDBNull(4) ? $"Automation run {status.ToString().ToLowerInvariant()}." : reader.GetString(4),
                        Purpose = reader.GetString(5), RequiresVerdict = reader.GetBoolean(6) };
                }
            if (hasActions) await ApplyDeterministicRetriesAsync(state, project, cards, byTrigger, ct);
        }
        var workflow = await ReadWorkflowStatusDataAsync(board, state, project, cards, allRows, hasRuns, ct);
        foreach (var card in cards) ApplyWorkflowStatuses(card, result[card.Id], workflow);
        return result;
    }
}
