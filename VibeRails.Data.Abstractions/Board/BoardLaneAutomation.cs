namespace VibeRails.Services.Board;

/// <summary>Existing Automations selected for a lane. Only future lane entries trigger them.</summary>
public sealed record BoardLaneAutomation(IReadOnlyList<long> JobIds, int Revision = 0)
{
    // Retained for clients that only support one Automation.
    public long? JobId => JobIds.Count == 0 ? null : JobIds[0];
}

/// <summary>A durable lane entry. EventKey identifies this entry even if the card moves again.</summary>
public sealed record BoardLaneAutomationEvent(
    string CardId, long JobId, string EventKey, string ProjectPath, string TriggerKey, bool IsCurrent);

public partial interface IBoardStore
{
    Task<BoardLaneAutomation?> GetLaneAutomationAsync(string projectPath, string columnId, CancellationToken cancellationToken = default);
    Task<BoardLaneAutomation?> SaveLaneAutomationAsync(string projectPath, string columnId, IReadOnlyList<long> jobIds, int expectedRevision, CancellationToken cancellationToken = default);
    /// <summary>Reads a bounded batch without claiming or removing entries.</summary>
    Task<IReadOnlyList<BoardLaneAutomationEvent>> GetDueLaneAutomationsAsync(DateTime nowUtc, CancellationToken cancellationToken = default);
    /// <summary>Removes only the observed entry; a subsequent lane entry must survive.</summary>
    Task AcknowledgeLaneAutomationAsync(BoardLaneAutomationEvent entry, CancellationToken cancellationToken = default);
}
