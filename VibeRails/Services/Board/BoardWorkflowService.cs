using VibeRails.DTOs;

namespace VibeRails.Services.Board;

/// <summary>Explicit agent progress and decisions for the current ordered lane workflow.</summary>
public sealed class BoardWorkflowService(VibeRails.Services.Launches.BoardLaunchWorkflow? launches = null)
{
    /// <summary>Record a scoped decision; a review pass must cite the caller's current canonical evidence.</summary>
    public async Task ReportAsync(string project, string cardId, string workspace, BoardAuthor author,
        string status, string summary, string? eventKey, string? reviewId, CancellationToken ct)
    {
        if (author.SessionId is null) throw new BoardValidationException("This tool requires a current VibeRails agent session.");
        if (status == "skipped") throw new BoardValidationException("Only the user can skip a lane entry.");
        if (launches is not null && await launches.TryReportAsync(project, cardId, workspace, author, status, summary, eventKey, reviewId, ct)) return;
        throw new BoardValidationException("No current Agent/Action lane entry matches this session. Legacy Automation entries are retired.");
    }
}
