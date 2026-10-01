using System.Text.Json;
using VibeRails.Data.Sqlite;
using VibeRails.DTOs;
using VibeRails.Services;

namespace VibeRails.DB;

public sealed partial class JobStore
{
    private async Task<string> GetDefaultReviewScopeAsync(JobDefinitionRecord job, int workerId, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT EXISTS (SELECT 1 FROM BoardAutomationRecipes
                WHERE ProjectPath = $project{ProjectPathCollation} AND JobId = $job
                  AND WorkerId = $worker AND RecipeId = $recipe);
            """;
        command.Parameters.AddWithValue("$project", job.ProjectPath);
        command.Parameters.AddWithValue("$job", job.Id);
        command.Parameters.AddWithValue("$worker", workerId);
        command.Parameters.AddWithValue("$recipe", BoardReviewDefaults.RecipeId);
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct)) != 0 ? BoardReviewDefaults.ReviewScope : "working-tree";
    }

    public async Task<long> EnsureBoardReviewRecipeAsync(string projectPath, string columnId, string recipeId, CancellationToken cancellationToken = default)
    {
        if (recipeId != BoardReviewDefaults.RecipeId) throw new ArgumentException("Unknown local Board recipe.", nameof(recipeId));
        var project = NormalizeProjectPath(projectPath);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT JobId FROM BoardAutomationRecipes WHERE ProjectPath = $project{ProjectPathCollation} AND ColumnId = $column AND RecipeId = $recipe;";
        command.Parameters.AddWithValue("$project", project);
        command.Parameters.AddWithValue("$column", columnId);
        command.Parameters.AddWithValue("$recipe", recipeId);
        var existing = await command.ExecuteScalarAsync(cancellationToken);
        // Retain the receipt even if the user deletes the Automation: recovery must never recreate it.
        if (existing is not null) return Convert.ToInt64(existing);
        var now = DateTime.UtcNow;
        // Pick a readable unused label under the same writer transaction. Never reuse a row by name.
        var workerName = BoardReviewDefaults.Name;
        command.Parameters.AddWithValue("$workerName", workerName);
        command.CommandText = "SELECT EXISTS (SELECT 1 FROM Environments WHERE CustomName = $workerName COLLATE NOCASE);";
        for (var suffix = 2; Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) != 0; suffix++)
        {
            workerName = $"{BoardReviewDefaults.Name} {suffix}";
            command.Parameters["$workerName"].Value = workerName;
        }
        command.CommandText = """
            INSERT INTO Environments (CustomName, LLM, CustomPrompt, CreatedUTC, LastUsedUTC,
                AutomationWorker, ProjectPath, Purpose, ReviewerRoutingJson)
            VALUES ($workerName, $llm, $prompt, $now, $now, 1, $project, 'code_review', $routing)
            RETURNING Id;
            """;
        command.Parameters.AddWithValue("$llm", (int)LLM.Codex);
        command.Parameters.AddWithValue("$prompt", BoardReviewDefaults.Prompt);
        command.Parameters.AddWithValue("$now", ToDb(now));
        command.Parameters.AddWithValue("$routing", JsonSerializer.Serialize(ReviewerRouting.SwitchDefault(), StorageJsonSerializerContext.Default.ReviewerRouting));
        var workerId = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
        command.CommandText = """
            INSERT INTO Jobs (Name, ProjectPath, EnvironmentId, TimeoutMinutes, Enabled, CreatedUTC, UpdatedUTC, Description)
            VALUES ($name, $project, $worker, 0, 1, $now, $now, $description) RETURNING Id;
            """;
        command.Parameters.AddWithValue("$name", workerName);
        command.Parameters.AddWithValue("$worker", workerId);
        command.Parameters.AddWithValue("$description", BoardReviewDefaults.Description);
        var jobId = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
        var actions = ReviewCheckDefaults.Create();
        actions.Add(new(null, JobActionKind.Worker, EnvironmentId: workerId));
        await ReplaceActionsAsync(connection, transaction, jobId, workerId, actions, now, cancellationToken);
        command.CommandText = "INSERT INTO BoardAutomationRecipes (ProjectPath, ColumnId, RecipeId, JobId, WorkerId) VALUES ($project, $column, $recipe, $job, $worker);";
        command.Parameters.AddWithValue("$job", jobId);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return jobId;
    }

    private const string BoardRecipeSchemaSql = """
        CREATE TABLE IF NOT EXISTS BoardAutomationRecipes (
            ProjectPath TEXT NOT NULL,
            ColumnId TEXT NOT NULL,
            RecipeId TEXT NOT NULL,
            JobId INTEGER NOT NULL,
            WorkerId INTEGER NOT NULL,
            PRIMARY KEY (ProjectPath, ColumnId, RecipeId)
        );
        """;
}
