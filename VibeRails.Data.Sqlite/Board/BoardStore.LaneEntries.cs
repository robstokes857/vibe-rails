using System.Globalization;
using Microsoft.Data.Sqlite;
using VibeRails.DTOs;

namespace VibeRails.Services.Board;

public sealed partial class BoardStore
{
    public async Task<IReadOnlyList<BoardPendingLaneAutomation>> GetPendingLaneAutomationsAsync(
        string projectPath, string cardId, CancellationToken cancellationToken = default)
    {
        var project = NormalizeProjectPath(projectPath);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT p.JobId, p.ColumnId, p.DueUnixMs FROM BoardPendingAutomations p
            JOIN BoardCards c ON c.Id = p.CardId
            WHERE p.CardId = $card AND c.ProjectPath = $project{ProjectPathCollation}
            UNION ALL
            SELECT p.JobId, p.ColumnId, p.DueUnixMs FROM BoardPendingAdditionalAutomations p
            JOIN BoardCards c ON c.Id = p.CardId
            WHERE p.CardId = $card AND c.ProjectPath = $project{ProjectPathCollation}
            ORDER BY 3, 1;
            """;
        command.Parameters.AddWithValue("$card", cardId);
        command.Parameters.AddWithValue("$project", project);
        var pending = new List<BoardPendingLaneAutomation>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            pending.Add(new(reader.GetInt64(0), reader.GetString(1),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)).UtcDateTime));
        return pending;
    }

    // Jobs are local Automation definitions in state.db. This is a read for wording only: the
    // scheduler re-evaluates every gate when the entry settles (JobStore.InsertRunAsync), and a
    // stdio MCP host may open a state.db that has never held Automations, so absent tables
    // degrade to "definition unavailable" rather than failing the card read or move.
    public async Task<IReadOnlyList<BoardLaneAutomationDefinition>> DescribeLaneAutomationsAsync(
        string projectPath, IReadOnlyList<long> jobIds, CancellationToken cancellationToken = default)
    {
        var results = new List<BoardLaneAutomationDefinition>(jobIds.Count);
        if (jobIds.Count == 0) return results;
        var project = NormalizeProjectPath(projectPath);
        var ids = string.Join(", ", jobIds.Distinct().Select(id => id.ToString(CultureInfo.InvariantCulture)));

        await using var state = await OpenStateAsync(cancellationToken);
        var hasJobs = await TableExistsAsync(state, "Jobs", cancellationToken)
            && await TableExistsAsync(state, "JobActions", cancellationToken)
            && await TableExistsAsync(state, "JobRuns", cancellationToken);
        var hasEnvironments = hasJobs && await TableExistsAsync(state, "Environments", cancellationToken);

        var definitions = new Dictionary<long, BoardLaneAutomationDefinition>();
        var workers = new Dictionary<long, (string Name, string Cli)>();
        var scripts = new Dictionary<long, List<string>>();
        var runs = new Dictionary<long, (string Id, bool Running)>();
        if (hasJobs)
        {
            var actionEnvironment = hasEnvironments ? "e.CustomName, COALESCE(e.LLM, 0)" : "NULL, 0";
            var actionJoin = hasEnvironments ? "LEFT JOIN Environments e ON e.Id = a.EnvironmentId" : string.Empty;
            await using (var actions = state.CreateCommand())
            {
                actions.CommandText = $"""
                    SELECT a.JobId, a.Kind, a.ScriptPath, {actionEnvironment}
                    FROM JobActions a {actionJoin}
                    WHERE a.JobId IN ({ids}) ORDER BY a.JobId, a.Position;
                    """;
                await using var reader = await actions.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var jobId = reader.GetInt64(0);
                    if ((JobActionKind)reader.GetInt32(1) == JobActionKind.Worker)
                    {
                        if (!reader.IsDBNull(3))
                            workers[jobId] = (reader.GetString(3), LlmParser.ToWireName((LLM)reader.GetInt32(4)));
                    }
                    else if (!reader.IsDBNull(2))
                    {
                        if (!scripts.TryGetValue(jobId, out var list)) scripts[jobId] = list = [];
                        list.Add(reader.GetString(2));
                    }
                }
            }

            await using (var active = state.CreateCommand())
            {
                active.CommandText = $"""
                    SELECT JobId, Id, Status FROM JobRuns
                    WHERE JobId IN ({ids}) AND Status IN ($queued, $running) ORDER BY QueuedUTC;
                    """;
                active.Parameters.AddWithValue("$queued", (int)JobRunStatus.Queued);
                active.Parameters.AddWithValue("$running", (int)JobRunStatus.Running);
                await using var reader = await active.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var jobId = reader.GetInt64(0);
                    var running = reader.GetInt32(2) == (int)JobRunStatus.Running;
                    // A running run outranks a queued one in the report; otherwise the earliest wins.
                    if (!runs.TryGetValue(jobId, out var existing) || (running && !existing.Running))
                        runs[jobId] = (reader.GetString(1), running);
                }
            }

            var jobEnvironment = hasEnvironments
                ? "e.CustomName, COALESCE(e.LLM, 0), COALESCE(NULLIF(TRIM(e.CustomPrompt), ''), '')"
                : "NULL, 0, ''";
            var jobJoin = hasEnvironments ? "LEFT JOIN Environments e ON e.Id = j.EnvironmentId" : string.Empty;
            await using (var jobs = state.CreateCommand())
            {
                jobs.CommandText = $"""
                    SELECT j.Id, j.Name, j.Enabled, j.DeletedUTC IS NOT NULL,
                           j.ProjectPath = $project{ProjectPathCollation},
                           EXISTS (SELECT 1 FROM JobActions a WHERE a.JobId = j.Id),
                           {jobEnvironment}
                    FROM Jobs j {jobJoin}
                    WHERE j.Id IN ({ids});
                    """;
                jobs.Parameters.AddWithValue("$project", project);
                await using var reader = await jobs.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var jobId = reader.GetInt64(0);
                    (string? Name, string? Cli) worker = workers.TryGetValue(jobId, out var action) ? action
                        : reader.IsDBNull(6) ? (null, null)
                        : (reader.GetString(6), LlmParser.ToWireName((LLM)reader.GetInt32(7)));
                    var run = runs.TryGetValue(jobId, out var found) ? found : default;
                    definitions[jobId] = new BoardLaneAutomationDefinition(
                        jobId,
                        reader.GetString(1),
                        reader.GetBoolean(2),
                        reader.GetBoolean(3),
                        reader.GetBoolean(4),
                        reader.GetBoolean(5),
                        worker.Name,
                        worker.Cli ?? string.Empty,
                        reader.GetString(8),
                        scripts.TryGetValue(jobId, out var paths) ? paths : [],
                        run.Id,
                        run.Running);
                }
            }
        }

        foreach (var jobId in jobIds)
            results.Add(definitions.TryGetValue(jobId, out var definition)
                ? definition
                : new BoardLaneAutomationDefinition(jobId, null, false, false, false, false, null, string.Empty, string.Empty, [], null, false));
        return results;
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, string table, CancellationToken cancellationToken) =>
        await ScalarLongAsync(connection, null,
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $table",
            ("$table", table), cancellationToken) > 0;
}
