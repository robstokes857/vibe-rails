namespace VibeRails.Services.Board;

/// <summary>A new board's durable, lane-ID-bound request for its local starter recipe.</summary>
public sealed record BoardStarterWorkflow(string ColumnId, string RecipeId);

public partial interface IBoardStore
{
    /// <summary>Only new, untouched local template lanes can have pending starter work.</summary>
    Task<IReadOnlyList<BoardStarterWorkflow>> GetPendingStarterWorkflowsAsync(string projectPath, CancellationToken cancellationToken = default);
    /// <summary>Assigns once to untouched settings, or retires an intent when jobId is null. Creates no card entries.</summary>
    Task CompleteStarterWorkflowAsync(string projectPath, string columnId, long? jobId, CancellationToken cancellationToken = default);
}
