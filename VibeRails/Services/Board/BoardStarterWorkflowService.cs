using VibeRails.DB;

namespace VibeRails.Services.Board;

/// <summary>Recoverable local recipe installation across independent Board and state commits.</summary>
public sealed class BoardStarterWorkflowService(IBoardStore boards, IJobStore jobs, ILogger<BoardStarterWorkflowService> logger)
{
    /// <summary>Makes new-board recipes available without selecting them; failed installs remain retryable.</summary>
    public async Task RecoverAsync(string project, CancellationToken ct)
    {
        foreach (var seed in await boards.GetPendingStarterWorkflowsAsync(project, ct))
        {
            try
            {
                await jobs.EnsureBoardReviewRecipeAsync(project, seed.ColumnId, seed.RecipeId, ct);
                // Keep Switch reviewer available in the picker. Only an explicit lane settings save selects it.
                await boards.CompleteStarterWorkflowAsync(project, seed.ColumnId, null, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Board creation succeeded. Do not return a creation error and invite a duplicate board.
                logger.LogWarning(ex, "Starter review setup is pending for lane {ColumnId}", seed.ColumnId);
            }
        }
    }
}
