using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services.Jobs;

namespace VibeRails.Services.Board;

/// <summary>Run an existing project Automation and retain the card in its immutable run context.</summary>
public sealed class BoardCardAutomationService(IBoardStore boards, IJobStore jobs, IJobService runner)
{
    /// <summary>Rerun only the failed entry the user selected, retaining its immutable run snapshot.</summary>
    public async Task<JobActionResponse?> RerunAsync(string projectPath, string cardKeyOrId,
        long jobId, string eventKey, CancellationToken cancellationToken)
    {
        if (jobId <= 0 || string.IsNullOrWhiteSpace(eventKey) || eventKey.Length > 100)
            throw new BoardValidationException("Choose a failed lane Automation.");
        var card = await boards.FindCardAsync(projectPath, cardKeyOrId, cancellationToken);
        if (card is null) return null;
        var status = (await boards.GetLaneAutomationStatusesAsync(projectPath, card.Id, cancellationToken))
            .SingleOrDefault(entry => entry.JobId == jobId && entry.EventKey == eventKey);
        if (status is null || !status.CanRerun)
            throw new BoardConflictException("This step cannot be rerun. Refresh to see its current status.");
        var entry = new BoardLaneAutomationEvent(card.Id, jobId, eventKey, card.ProjectPath,
            $"board-lane:{card.Key}:{status.ColumnId}:{eventKey}", true);
        var result = await runner.RerunBoardLaneAsync(entry, status.RunId, cancellationToken);
        // Skip and the run commit live in separate databases. A racing skip must stop the
        // newly committed attempt before its successor can proceed.
        await CancelSkippedRerunAsync(projectPath, card.Id, jobId, eventKey, CancellationToken.None);
        return result;
    }

    private async Task CancelSkippedRerunAsync(string projectPath, string cardId, long jobId,
        string eventKey, CancellationToken cancellationToken)
    {
        var current = (await boards.GetLaneAutomationStatusesAsync(projectPath, cardId, cancellationToken))
            .FirstOrDefault(entry => entry.JobId == jobId && entry.EventKey == eventKey);
        if (current is { StepStatus: BoardStepStatus.Stopping, RunId: not null })
            await jobs.RequestCancelAsync(current.RunId, cancellationToken);
    }

    /// <summary>Skip one current step, requesting cancellation before a running step releases its successor.</summary>
    public async Task<BoardCardAutomationsResponse?> SkipAsync(string projectPath, string cardKeyOrId,
        long jobId, string eventKey, CancellationToken cancellationToken)
    {
        if (jobId <= 0 || string.IsNullOrWhiteSpace(eventKey) || eventKey.Length > 100)
            throw new BoardValidationException("Choose a waiting lane Automation.");
        var card = await boards.FindCardAsync(projectPath, cardKeyOrId, cancellationToken);
        if (card is null) return null;
        var status = (await boards.GetLaneAutomationStatusesAsync(projectPath, card.Id, cancellationToken))
            .FirstOrDefault(entry => entry.JobId == jobId && entry.EventKey == eventKey);
        if (status is null || !(status.CanSkip || status.Status == BoardStepStatus.Waiting && status.RunId is null))
            throw new BoardConflictException("This step cannot be skipped. Refresh to see its current status.");
        if (status.RunId is null && status.Status == BoardStepStatus.Waiting)
        {
            var entry = new BoardLaneAutomationEvent(card.Id, jobId, eventKey, card.ProjectPath,
                $"board-lane:{card.Key}:{status.ColumnId}:{eventKey}", true);
            if (!await boards.SkipLaneAutomationAsync(entry, BoardAuthor.User(),
                $"Skipped lane Automation “{status.Name}” for this card's current workflow.", cancellationToken))
                throw new BoardConflictException("This entry changed. Refresh to see its current status.");
        }
        else
        {
            if (!await boards.ReportLaneStepAsync(projectPath, card.Id,
                new(eventKey, jobId, BoardStepStatus.Skipped, $"User skipped {status.Name} for this card's current workflow.", status.RunId),
                BoardAuthor.User(), cancellationToken))
                throw new BoardConflictException("This entry changed. Refresh to see its current status.");
            if (status.RunId is not null && status.Status is BoardStepStatus.Queued or BoardStepStatus.Running)
                await jobs.RequestCancelAsync(status.RunId, cancellationToken);
        }
        await CancelSkippedRerunAsync(projectPath, card.Id, jobId, eventKey, CancellationToken.None);
        return await GetAsync(projectPath, card.Id, cancellationToken);
    }

    public async Task<BoardCardAutomationsResponse?> GetAsync(string projectPath, string cardKeyOrId,
        CancellationToken cancellationToken)
    {
        var card = await boards.GetCardDetailAsync(projectPath, cardKeyOrId, cancellationToken);
        if (card is null) return null;
        var catalog = await jobs.GetJobsAsync(projectPath, cancellationToken: cancellationToken);
        var recordings = card.Sessions.Select(session => session.SessionId).Distinct(StringComparer.Ordinal).ToList();
        var runs = await jobs.GetBoardCardRunsAsync(projectPath, card.Card.Key, cancellationToken, recordings);
        var entries = await boards.GetLaneAutomationStatusesAsync(projectPath, card.Card.Id, cancellationToken);
        // Once a recording is linked, the existing Automations rail owns its open/replay action.
        // Keep queued and failed-before-launch runs visible even when they have no recording.
        return new(catalog.Select(job => new BoardAutomationOption(job.Id, job.Name, job.Enabled)).ToList(),
            runs.Where(run => run.Purpose != "code_review").Select(run => new BoardCardAutomationRunResponse(run.Id, run.JobName, run.Status,
                    run.QueuedUtc, run.ErrorMessage)).ToList(), entries);
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
