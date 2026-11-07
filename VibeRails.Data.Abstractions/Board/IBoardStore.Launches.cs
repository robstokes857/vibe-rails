namespace VibeRails.Services.Board;

public sealed record BoardLaunchSelection(string DefinitionId, string DefinitionType, string Gate = "result");
public sealed record BoardLaunchLane(IReadOnlyList<BoardLaunchSelection> Selections, long Revision);
public sealed record BoardLaunchEvent(string Id, string ProjectPath, string CardId, string ColumnId, string? BoardId,
    string DefinitionId, string DefinitionType, string Gate, int Position, DateTime DueUtc, bool Current,
    string? RunId, string ProcessStatus, string? Verdict, bool SkipRequested, string? Summary);

public partial interface IBoardStore
{
    Task<BoardLaunchLane?> GetLaunchLaneAsync(string project, string columnId, CancellationToken ct = default) => Task.FromResult<BoardLaunchLane?>(null);
    Task<BoardLaunchLane?> SaveLaunchLaneAsync(string project, string columnId, BoardLaunchLane lane, CancellationToken ct = default) => throw new NotSupportedException();
    Task<IReadOnlyList<BoardLaunchEvent>> GetLaunchEventsAsync(string? project = null, string? cardId = null, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<BoardLaunchEvent>>([]);
    Task<bool> AcknowledgeLaunchEventAsync(string id, string runId, CancellationToken ct = default) => throw new NotSupportedException();
    Task<bool> UpdateLaunchEventAsync(string id, string runId, string processStatus, CancellationToken ct = default) => throw new NotSupportedException();
    Task<bool> RetryLaunchEventAsync(string id, string previousRun, string newRun, CancellationToken ct = default) => throw new NotSupportedException();
    Task<bool> ReportLaunchEventAsync(string project, string cardId, string id, string verdict, string summary,
        string? sessionId, string? reviewId, bool userSkip = false, CancellationToken ct = default, string? expectedRunId = null) => throw new NotSupportedException();
}
