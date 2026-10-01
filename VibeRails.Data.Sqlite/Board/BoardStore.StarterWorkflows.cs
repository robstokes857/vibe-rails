using Microsoft.Data.Sqlite;

namespace VibeRails.Services.Board;

public sealed partial class BoardStore
{
    public async Task<IReadOnlyList<BoardStarterWorkflow>> GetPendingStarterWorkflowsAsync(string projectPath, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT s.ColumnId, s.RecipeId FROM BoardStarterWorkflows s
            JOIN BoardColumns c ON c.Id = s.ColumnId
            LEFT JOIN BoardLaneAutomations a ON a.ColumnId = c.Id
            WHERE c.ProjectPath = $project{ProjectPathCollation} AND s.Completed = 0
              AND a.ColumnId IS NULL;
            """;
        command.Parameters.AddWithValue("$project", NormalizeProjectPath(projectPath));
        var result = new List<BoardStarterWorkflow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(new(reader.GetString(0), reader.GetString(1)));
        return result;
    }

    public async Task CompleteStarterWorkflowAsync(string projectPath, string columnId, long? jobId, CancellationToken cancellationToken = default)
    {
        if (jobId <= 0) throw new ArgumentOutOfRangeException(nameof(jobId));
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            INSERT INTO BoardLaneAutomations (ColumnId, JobId, Revision)
            SELECT c.Id, $job, 1 FROM BoardStarterWorkflows s JOIN BoardColumns c ON c.Id = s.ColumnId
            WHERE c.ProjectPath = $project{ProjectPathCollation} AND c.Id = $column AND s.Completed = 0 AND $job IS NOT NULL
              AND NOT EXISTS (SELECT 1 FROM BoardLaneAutomations a WHERE a.ColumnId = c.Id);
            UPDATE BoardStarterWorkflows SET Completed = 1 WHERE ColumnId = $column
              AND EXISTS (SELECT 1 FROM BoardColumns c WHERE c.Id = $column AND c.ProjectPath = $project{ProjectPathCollation});
            """;
        command.Parameters.AddWithValue("$project", NormalizeProjectPath(projectPath));
        command.Parameters.AddWithValue("$column", columnId);
        command.Parameters.AddWithValue("$job", (object?)jobId ?? DBNull.Value);
        // The insert trigger records completion in the same transaction. A concurrent user save wins.
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private const string StarterWorkflowSchemaSql = """
        CREATE TABLE IF NOT EXISTS BoardStarterWorkflows (
            ColumnId TEXT PRIMARY KEY REFERENCES BoardColumns(Id) ON DELETE CASCADE,
            RecipeId TEXT NOT NULL,
            Completed INTEGER NOT NULL DEFAULT 0
        );
        CREATE TRIGGER IF NOT EXISTS BoardStarterWorkflows_SettingsInsert AFTER INSERT ON BoardLaneAutomations BEGIN
            UPDATE BoardStarterWorkflows SET Completed = 1 WHERE ColumnId = NEW.ColumnId;
        END;
        CREATE TRIGGER IF NOT EXISTS BoardStarterWorkflows_SettingsUpdate AFTER UPDATE ON BoardLaneAutomations BEGIN
            UPDATE BoardStarterWorkflows SET Completed = 1 WHERE ColumnId = NEW.ColumnId;
        END;
        """;
}
