using System.Text.Json;
using Microsoft.Data.Sqlite;
using VibeRails.DTOs;

namespace VibeRails.Services.Board;

public sealed partial class BoardStore
{
    // Workflow eligibility still follows execution success. Display the evidence outcome for
    // check-only runs without turning advisory findings into a gate or approving a Worker.
    private static async Task ApplyLaneCheckOutcomesAsync(SqliteConnection board, SqliteConnection state,
        string project, Dictionary<string, List<BoardLaneAutomationStatus>> cards, CancellationToken ct)
    {
        var runIds = cards.Values.SelectMany(rows => rows)
            .Where(row => row.RunId is not null && !row.RequiresVerdict
                && row.StepStatus is BoardStepStatus.Passed or BoardStepStatus.Failed)
            .Select(row => row.RunId!).Distinct().ToList();
        if (runIds.Count == 0) return;

        var actions = new Dictionary<string, List<(string Id, JobActionKind Kind)>>();
        await using (var query = state.CreateCommand())
        {
            query.CommandText = $"""
                SELECT a.RunId, a.Id, a.Kind FROM JobRunActions a JOIN JobRuns r ON r.Id = a.RunId
                WHERE r.ProjectPath = $project{ProjectPathCollation}
                    AND a.RunId IN (SELECT value FROM json_each($runs)) ORDER BY a.Position;
                """;
            query.Parameters.AddWithValue("$project", project);
            query.Parameters.AddWithValue("$runs", JsonSerializer.Serialize(runIds, StorageJsonSerializerContext.Default.ListString));
            await using var reader = await query.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                if (!actions.TryGetValue(reader.GetString(0), out var list)) actions[reader.GetString(0)] = list = [];
                list.Add((reader.GetString(1), (JobActionKind)reader.GetInt32(2)));
            }
        }
        var checkRuns = actions.Where(pair => pair.Value.All(a => a.Kind is JobActionKind.CodeQuality or JobActionKind.Vca))
            .Select(pair => pair.Key).ToList();
        if (checkRuns.Count == 0) return;

        var checks = new Dictionary<(string Card, string Run, string Action), BoardCheckRecord>();
        await using (var query = board.CreateCommand())
        {
            query.CommandText = $"""
                SELECT e.SummaryJson FROM BoardChecks e JOIN BoardCards c ON c.Id = e.CardId
                WHERE c.ProjectPath = $project{ProjectPathCollation} AND c.DeletedUTC IS NULL
                    AND e.CardId IN (SELECT value FROM json_each($cards))
                    AND e.RunId IN (SELECT value FROM json_each($runs));
                """;
            query.Parameters.AddWithValue("$project", project);
            query.Parameters.AddWithValue("$cards", JsonSerializer.Serialize(cards.Keys.ToList(), StorageJsonSerializerContext.Default.ListString));
            query.Parameters.AddWithValue("$runs", JsonSerializer.Serialize(checkRuns, StorageJsonSerializerContext.Default.ListString));
            await using var reader = await query.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var check = JsonSerializer.Deserialize(reader.GetString(0), StorageJsonSerializerContext.Default.BoardCheckRecord)!;
                checks[(check.CardId, check.RunId, check.ActionId)] = check;
            }
        }
        foreach (var (cardId, rows) in cards)
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row.RunId is null || !checkRuns.Contains(row.RunId) || row.RequiresVerdict
                || row.StepStatus is not (BoardStepStatus.Passed or BoardStepStatus.Failed)) continue;
            var evidence = actions[row.RunId].Select(action => checks.GetValueOrDefault((cardId, row.RunId, action.Id))).ToList();
            // An execution failure stays visible even when it prevented an evidence save.
            if (row.Status == BoardStepStatus.Failed && evidence.All(check => check?.Status is not ("Failed to run" or "Cancelled"))) continue;
            var complete = evidence.All(check => check?.EndedUtc is not null);
            var saved = evidence.Where(check => check?.EndedUtc is not null).Select(check => check!).ToList();
            var status = CheckOutcome(saved.Select(check => check.Status).ToList());
            if (!complete && status is not ("Failed to run" or "Cancelled")) status = "Unknown";
            var summary = string.Join(" ", saved.Select(check => $"{check.Tool}: {check.Summary}"));
            rows[i] = row with { CheckStatus = status, Reason = complete ? summary
                : $"Check evidence is missing or incomplete; the full result is unavailable. {summary}".TrimEnd() };
        }
    }

    private static string CheckOutcome(IReadOnlyList<string> statuses)
    {
        if (statuses.Count == 0) return "Unknown";
        foreach (var outcome in new[] { "Failed to run", "Cancelled", "Findings", "Completed with limitations" })
            if (statuses.Contains(outcome)) return outcome;
        if (statuses.All(status => status == "Passed")) return "Passed";
        if (statuses.All(status => status == "Skipped/not applicable")) return "Skipped/not applicable";
        return statuses.All(status => status is "Passed" or "Skipped/not applicable") ? "Completed with limitations" : "Unknown";
    }
}
