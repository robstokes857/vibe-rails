namespace VibeRails.Services.Board;

/// <summary>Existing Automations selected for a lane. Only future lane entries trigger them.</summary>
public sealed record BoardLaneAutomation(IReadOnlyList<long> JobIds, int Revision = 0)
{
    // Retained for clients that only support one Automation.
    public long? JobId => JobIds.Count == 0 ? null : JobIds[0];
}

public partial interface IBoardStore
{
    Task<BoardLaneAutomation?> GetLaneAutomationAsync(string projectPath, string columnId, CancellationToken cancellationToken = default);
    Task<BoardLaneAutomation?> SaveLaneAutomationAsync(string projectPath, string columnId, IReadOnlyList<long> jobIds, int expectedRevision, CancellationToken cancellationToken = default);
}
