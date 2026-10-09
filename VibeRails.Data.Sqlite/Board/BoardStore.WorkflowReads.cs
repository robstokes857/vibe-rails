using System.Text.Json;
using Microsoft.Data.Sqlite;
using VibeRails.DTOs;

namespace VibeRails.Services.Board;

public sealed partial class BoardStore
{
    private sealed class WorkflowStatusData
    {
        public Dictionary<string, (string Workflow, int Position, bool Current)> Metadata { get; } = [];
        public Dictionary<string, (string Status, string Summary, string? Run)> Reports { get; } = [];
        public Dictionary<string, List<long>> Selections { get; } = [];
        public Dictionary<string, string> RunStatuses { get; } = [];
    }

    private static async Task<WorkflowStatusData> ReadWorkflowStatusDataAsync(SqliteConnection board,
        SqliteConnection state, string project, IReadOnlyList<LaneStatusCard> cards,
        List<BoardLaneAutomationStatus> rows, bool hasRuns, CancellationToken ct)
    {
        var data = new WorkflowStatusData();
        await using var query = board.CreateCommand();
        query.CommandText = """
            SELECT s.EventKey, s.JobId, w.Id, s.Position, w.Current AND w.ColumnId = c.ColumnId
            FROM BoardLaneWorkflows w JOIN BoardLaneWorkflowSteps s ON s.WorkflowId = w.Id
            JOIN BoardCards c ON c.Id = w.CardId
            WHERE w.CardId IN (SELECT value FROM json_each($cards))
                AND s.EventKey IN (SELECT value FROM json_each($events));
            """;
        query.Parameters.AddWithValue("$cards", JsonSerializer.Serialize(cards.Select(c => c.Id).ToList(), StorageJsonSerializerContext.Default.ListString));
        query.Parameters.AddWithValue("$events", JsonSerializer.Serialize(rows.Select(r => r.EventKey).Distinct().ToList(), StorageJsonSerializerContext.Default.ListString));
        await using (var reader = await query.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                data.Metadata[reader.GetString(0) + ":" + reader.GetInt64(1)] =
                    (reader.GetString(2), reader.GetInt32(3), reader.GetBoolean(4));
        query.CommandText = """
            SELECT r.EventKey, r.JobId, r.Status, r.Summary, r.RunId FROM BoardLaneStepReports r
            WHERE r.EventKey IN (SELECT value FROM json_each($events))
            AND r.Id = (SELECT MAX(latest.Id) FROM BoardLaneStepReports latest WHERE latest.EventKey = r.EventKey AND latest.JobId = r.JobId);
            """;
        await using (var reader = await query.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                data.Reports[reader.GetString(0) + ":" + reader.GetInt64(1)] =
                    (reader.GetString(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4));
        query.CommandText = $"""
            SELECT c.Id, a.JobId, extra.JobId FROM BoardColumns c
            LEFT JOIN BoardLaneAutomations a ON a.ColumnId = c.Id
            LEFT JOIN BoardLaneAdditionalAutomations extra ON extra.ColumnId = c.Id
            WHERE c.ProjectPath = $project{ProjectPathCollation} AND c.Id IN (SELECT value FROM json_each($lanes))
            ORDER BY c.Id, extra.Position;
            """;
        query.Parameters.AddWithValue("$project", project);
        query.Parameters.AddWithValue("$lanes", JsonSerializer.Serialize(cards.Select(c => c.ColumnId).Distinct().ToList(), StorageJsonSerializerContext.Default.ListString));
        await using (var reader = await query.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
            {
                if (!data.Selections.TryGetValue(reader.GetString(0), out var selected))
                {
                    data.Selections[reader.GetString(0)] = selected = [];
                    if (!reader.IsDBNull(1)) selected.Add(reader.GetInt64(1));
                }
                if (!reader.IsDBNull(2)) selected.Add(reader.GetInt64(2));
            }
        var reportRuns = data.Reports.Values.Where(r => r.Run is not null).Select(r => r.Run!).Distinct().ToList();
        if (hasRuns && reportRuns.Count > 0)
        {
            await using var runs = state.CreateCommand();
            runs.CommandText = $"""
                SELECT Id, Status FROM JobRuns WHERE ProjectPath = $project{ProjectPathCollation}
                    AND Id IN (SELECT value FROM json_each($runs));
                """;
            runs.Parameters.AddWithValue("$project", project);
            runs.Parameters.AddWithValue("$runs", JsonSerializer.Serialize(reportRuns, StorageJsonSerializerContext.Default.ListString));
            await using var reader = await runs.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                data.RunStatuses[reader.GetString(0)] = ((JobRunStatus)reader.GetInt32(1)).ToString();
        }
        return data;
    }
}
