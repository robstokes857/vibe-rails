using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services.Jobs;

namespace VibeRails.Services.Board;

/// <summary>Run an existing project Automation and retain the card in its immutable run context.</summary>
public sealed class BoardCardAutomationService(IBoardStore boards, IJobStore jobs, IJobService runner)
{
    public async Task<BoardCardAutomationsResponse?> GetAsync(string projectPath, string cardKeyOrId,
        CancellationToken cancellationToken)
    {
        var card = await boards.GetCardDetailAsync(projectPath, cardKeyOrId, cancellationToken);
        if (card is null) return null;
        var catalog = await jobs.GetJobsAsync(projectPath, cancellationToken: cancellationToken);
        var recordings = card.Sessions.Select(session => session.SessionId).Distinct(StringComparer.Ordinal).ToList();
        var runs = await jobs.GetBoardCardRunsAsync(projectPath, card.Card.Key, cancellationToken, recordings);
        // Once a recording is linked, the existing Automations rail owns its open/replay action.
        // Keep queued and failed-before-launch runs visible even when they have no recording.
        return new(catalog.Select(job => new BoardAutomationOption(job.Id, job.Name, job.Enabled)).ToList(),
            runs.Select(run => new BoardCardAutomationRunResponse(run.Id, run.JobName, run.Status,
                    run.QueuedUtc, run.ErrorMessage)).ToList());
    }

    public async Task<JobActionResponse?> RunAsync(string projectPath, string cardKeyOrId, long jobId,
        CancellationToken cancellationToken)
    {
        var card = await boards.GetCardDetailAsync(projectPath, cardKeyOrId, cancellationToken);
        if (card is null) return null;
        if (jobId <= 0) throw new BoardValidationException("Choose an Automation to run.");
        return await runner.RunForBoardCardAsync(jobId, projectPath, card.Card.Key, cancellationToken);
    }
}
