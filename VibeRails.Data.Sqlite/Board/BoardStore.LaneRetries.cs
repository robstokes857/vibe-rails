using System.Text.Json;
using Microsoft.Data.Sqlite;
using VibeRails.DTOs;

namespace VibeRails.Services.Board;

public sealed partial class BoardStore
{
    private async Task ApplyLaneRetriesAsync(SqliteConnection state, string project, IReadOnlyList<LaneStatusCard> cards,
        Dictionary<string, (List<BoardLaneAutomationStatus> Rows, int Index)> byTrigger, CancellationToken ct)
    {
        await using var query = state.CreateCommand();
        query.CommandText = $"""
            SELECT r.TriggerKey, r.Id, r.JobId, r.JobName, r.Status, r.ErrorMessage,
                EXISTS (SELECT 1 FROM JobRunActions a WHERE a.RunId = r.Id AND a.Kind = 0)
            FROM JobRuns r
            WHERE r.ProjectPath = $project{ProjectPathCollation} AND r.TriggerKind = $manual
              AND EXISTS (SELECT 1 FROM json_each($prefixes) WHERE instr(r.TriggerKey, value) = 1)
            ORDER BY r.rowid;
            """;
        query.Parameters.AddWithValue("$project", project);
        query.Parameters.AddWithValue("$manual", (int)JobTriggerKind.Manual);
        var prefixes = cards.SelectMany(card => new[] {
            $"{JobBoardContext.ManualPrefix}{card.Key}:lane:", $"{JobBoardContext.LaneRetryPrefix}{card.Key}:lane:" }).ToList();
        query.Parameters.AddWithValue("$prefixes", JsonSerializer.Serialize(prefixes, StorageJsonSerializerContext.Default.ListString));
        await using var reader = await query.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var trigger = JobBoardContext.GetLaneTriggerKey(JobTriggerKind.Manual, reader.GetString(0));
            if (trigger is null || !byTrigger.TryGetValue(trigger, out var target)) continue;
            var (rows, index) = target;
            if (rows[index].JobId != reader.GetInt64(2)
                || !rows[index].RequiresVerdict && rows[index].Status == BoardStepStatus.Succeeded) continue;
            var status = (JobRunStatus)reader.GetInt32(4);
            rows[index] = rows[index] with { RunId = reader.GetString(1), Name = reader.GetString(3),
                Status = status.ToString(), RequiresVerdict = reader.GetBoolean(6), Reason = reader.IsDBNull(5)
                    ? $"Automation retry {status.ToString().ToLowerInvariant()}." : reader.GetString(5) };
        }
    }
}
