using VibeRails.DTOs;
using VibeRails.Services.Jobs;

namespace VibeRails.Services.Board;

/// <summary>Links a lane-triggered Automation's replay to the card that queued it.</summary>
internal static class BoardAutomationSessionLinker
{
    internal static async Task LinkAsync(IBoardStore boards, JobRunRecord run, string sessionId,
        string? tabId = null, CancellationToken cancellationToken = default)
    {
        var cardKey = JobRunner.GetBoardCardKey(run);
        if (cardKey is null)
            return;
        var card = await boards.GetCardDetailAsync(run.ProjectPath, cardKey, cancellationToken);
        if (card is null || card.Sessions.Any(session => session.SessionId == sessionId))
            return;

        var cli = run.Llm == LLM.NotSet ? "shell" : run.Llm.ToString().ToLowerInvariant();
        var selection = run.EnvironmentId is int environmentId ? $"env:{environmentId}:{cli}" : string.Empty;
        try
        {
            await boards.LinkSessionAsync(run.ProjectPath, card.Card.Id, sessionId, tabId, selection,
                cli, $"Automation: {run.JobName}", BoardSessionRecord.LaunchOrigin, cancellationToken);
        }
        catch (BoardConflictException)
        {
            // A concurrent callback can link the same pair. Keep unrelated/project conflicts visible.
            var current = await boards.GetCardDetailAsync(run.ProjectPath, cardKey, cancellationToken);
            if (current?.Sessions.Any(session => session.SessionId == sessionId) != true)
                throw;
        }
    }
}
