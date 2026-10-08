using Microsoft.Data.Sqlite;
using VibeRails.DTOs;

namespace VibeRails.Services.Board;

public sealed partial class BoardStore
{
    private async Task ApplyDeterministicRetriesAsync(SqliteConnection state, BoardCardRecord card,
        List<BoardLaneAutomationStatus> rows, CancellationToken ct)
    {
        await using var query = state.CreateCommand();
        query.CommandText = $"""
            SELECT r.TriggerKey, r.Id, r.JobId, r.JobName, r.Status, r.ErrorMessage
            FROM JobRuns r
            WHERE r.ProjectPath = $project{ProjectPathCollation} AND r.TriggerKind = $manual
              AND (instr(r.TriggerKey, $cardPrefix) = 1 OR instr(r.TriggerKey, $retryPrefix) = 1)
              AND NOT EXISTS (SELECT 1 FROM JobRunActions a WHERE a.RunId = r.Id AND a.Kind = 0)
            ORDER BY r.rowid;
            """;
        query.Parameters.AddWithValue("$project", NormalizeProjectPath(card.ProjectPath));
        query.Parameters.AddWithValue("$manual", (int)JobTriggerKind.Manual);
        query.Parameters.AddWithValue("$cardPrefix", $"{JobBoardContext.ManualPrefix}{card.Key}:lane:");
        query.Parameters.AddWithValue("$retryPrefix", $"{JobBoardContext.LaneRetryPrefix}{card.Key}:lane:");
        await using var reader = await query.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var trigger = JobBoardContext.GetLaneTriggerKey(JobTriggerKind.Manual, reader.GetString(0));
            var index = rows.FindIndex(r => r.JobId == reader.GetInt64(2)
                && $"board-lane:{card.Key}:{r.ColumnId}:{r.EventKey}" == trigger);
            if (index < 0 || rows[index].RequiresVerdict || rows[index].Status == "Succeeded") continue;
            var status = (JobRunStatus)reader.GetInt32(4);
            rows[index] = rows[index] with { RunId = reader.GetString(1), Name = reader.GetString(3),
                Status = status.ToString(), Reason = reader.IsDBNull(5)
                    ? $"Automation retry {status.ToString().ToLowerInvariant()}." : reader.GetString(5) };
        }
    }
}
