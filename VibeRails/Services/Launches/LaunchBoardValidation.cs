using VibeRails.Launches;
using VibeRails.Services.Board;
namespace VibeRails.Services.Launches;

public static class LaunchBoardValidation
{
    public static async Task ValidateAsync(IBoardStore board, LaunchRun run, CancellationToken ct)
    {
        var context = run.Snapshot.Context;
        if (context.BoardId is not null) await BoardAccess.RequireWritableAsync(board, context.ProjectPath, context.BoardId, ct);
        if (context.CardId is null) return;
        var card = await board.FindCardAsync(context.ProjectPath, context.CardId, ct);
        if (card is null || card.BoardId != context.BoardId)
            throw new LaunchValidationException("The run's Board card is no longer available.");
        await BoardAccess.RequireWritableAsync(board, context.ProjectPath, card.BoardId, ct);
        if (context.LaneEntryId is not null)
        {
            var entries = await board.GetLaunchEventsAsync(context.ProjectPath, card.Id, ct);
            var entry = entries.SingleOrDefault(e => e.Id == context.LaneEntryId);
            if (entry is null || entry.RunId != run.Id || !entry.Current || entry.SkipRequested || card.ColumnId != entry.ColumnId
                || entries.Any(e => e.Current && e.Position < entry.Position && !BoardLaunchWorkflow.ReleasesNext(e)))
                throw new LaunchValidationException("The lane entry is no longer eligible to run.");
        }
    }

}
