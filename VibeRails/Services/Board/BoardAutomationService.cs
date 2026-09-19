using VibeRails.DB;
using VibeRails.DTOs;

namespace VibeRails.Services.Board;

/// <summary>Project-scoped lane settings and existing Automation choices for the dashboard.</summary>
public sealed class BoardAutomationService(IBoardStore boards, IJobStore jobs)
{
    public async Task<BoardLaneAutomationResponse?> GetAsync(string projectPath, string columnId, CancellationToken cancellationToken)
    {
        var setting = await boards.GetLaneAutomationAsync(projectPath, columnId, cancellationToken);
        if (setting is null) return null;
        var catalog = await jobs.GetJobsAsync(projectPath, cancellationToken: cancellationToken);
        return new(setting.JobId, setting.Revision, catalog.Select(job => new BoardAutomationOption(job.Id, job.Name, job.Enabled)).ToList());
    }

    public Task<BoardLaneAutomation?> SaveAsync(string projectPath, string columnId, UpdateBoardLaneAutomationRequest request, CancellationToken cancellationToken)
    {
        if (request.ExpectedRevision is null)
            throw new BoardValidationException("The expected lane automation revision is required.");
        return boards.SaveLaneAutomationAsync(projectPath, columnId, request.JobId, request.ExpectedRevision.Value, cancellationToken);
    }
}
