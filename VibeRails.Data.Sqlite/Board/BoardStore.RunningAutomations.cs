using VibeRails.Data.Sqlite;
using VibeRails.DTOs;

namespace VibeRails.Services.Board;

public sealed partial class BoardStore
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<BoardRunningAutomation>> GetRunningAutomationsAsync(string projectPath,
        CancellationToken cancellationToken = default)
    {
        var project = NormalizeProjectPath(projectPath);
        var entries = new List<(string CardKey, string? ColumnId)>();
        await using (var state = await OpenStateAsync(cancellationToken))
        {
            // Board-only hosts may not have initialized Jobs. Never initialize it just for a read.
            if (!SqliteSchema.HasColumn(state, null, "JobRuns", "TriggerKey")) return [];
            await using var command = state.CreateCommand();
            command.CommandText = $"""
                SELECT TriggerKind, TriggerKey FROM JobRuns
                WHERE ProjectPath = $project{ProjectPathCollation} AND Status = $running AND DeletedUTC IS NULL;
                """;
            command.Parameters.AddWithValue("$project", project);
            command.Parameters.AddWithValue("$running", (int)JobRunStatus.Running);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var kind = (JobTriggerKind)reader.GetInt32(0);
                var trigger = reader.GetString(1);
                var key = JobBoardContext.GetCardKey(kind, trigger);
                if (key is null) continue;
                var parts = trigger.Split(':');
                entries.Add((key, kind == JobTriggerKind.BoardLane && parts.Length == 4 ? parts[2] : null));
            }
        }
        if (entries.Count == 0) return [];
        // Separate read connections: no ATTACH, writer transaction, or historical conversion.
        var result = new List<BoardRunningAutomation>();
        await using var board = await OpenAsync(cancellationToken);
        foreach (var entry in entries)
        {
            await using var command = board.CreateCommand();
            command.CommandText = $"""
                SELECT c.Id, lane.BoardId, origin.Id FROM BoardCards c
                {CardPrefixJoinSql}
                JOIN BoardColumns lane ON lane.Id = c.ColumnId AND lane.ProjectPath = c.ProjectPath
                LEFT JOIN BoardColumns origin ON origin.Id = $column AND origin.ProjectPath = c.ProjectPath
                    AND origin.BoardId = lane.BoardId
                WHERE c.ProjectPath = $project{ProjectPathCollation} AND c.DeletedUTC IS NULL
                    AND {CardKeySql} = $key;
                """;
            command.Parameters.AddWithValue("$project", project);
            command.Parameters.AddWithValue("$key", entry.CardKey);
            command.Parameters.AddWithValue("$column", (object?)entry.ColumnId ?? DBNull.Value);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
                result.Add(new(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
        }
        return result;
    }
}
