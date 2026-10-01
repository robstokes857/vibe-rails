using VibeRails.DB;
using VibeRails.Utils;

namespace VibeRails.Services.Board;

/// <summary>Recoverable local recipe installation across independent Board and state commits.</summary>
public sealed class BoardStarterWorkflowService(IBoardStore boards, IJobStore jobs, ILogger<BoardStarterWorkflowService> logger)
{
    /// <summary>Finishes only durable new-board requests; failed installs remain visible and retryable.</summary>
    public async Task RecoverAsync(string project, CancellationToken ct)
    {
        foreach (var seed in await boards.GetPendingStarterWorkflowsAsync(project, ct))
        {
            try
            {
                var job = await jobs.EnsureBoardReviewRecipeAsync(project, seed.ColumnId, seed.RecipeId, ct);
                var definition = await jobs.GetJobAsync(job, ct);
                await boards.CompleteStarterWorkflowAsync(project, seed.ColumnId,
                    definition is { DeletedUtc: null, Enabled: true } && ProjectPathComparer.Matches(definition.ProjectPath, project)
                        ? job : null, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Board creation succeeded. Do not return a creation error and invite a duplicate board.
                logger.LogWarning(ex, "Starter review setup is pending for lane {ColumnId}", seed.ColumnId);
            }
        }
    }
}
