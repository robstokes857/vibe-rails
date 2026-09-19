namespace VibeRails.Services.Board;

/// <summary>One optional existing Automation per lane. Only future lane entries trigger it.</summary>
public sealed record BoardLaneAutomation(long? JobId, int Revision = 0);

public partial interface IBoardStore
{
    Task<BoardLaneAutomation?> GetLaneAutomationAsync(string projectPath, string columnId, CancellationToken cancellationToken = default);
    Task<BoardLaneAutomation?> SaveLaneAutomationAsync(string projectPath, string columnId, long? jobId, int expectedRevision, CancellationToken cancellationToken = default);
}
