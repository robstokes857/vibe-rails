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

/// <summary>
/// A lane Automation read from its local definition so agents can be told what a lane entry
/// triggers (VB-34). <see cref="Name"/> is null when the definition cannot be read: the Jobs
/// table is absent (a fresh stdio host) or the row is gone. The remaining flags mirror the gate
/// the scheduler applies when the entry settles, so callers can say why nothing would run.
/// </summary>
public sealed record BoardLaneAutomationDefinition(
    long JobId,
    string? Name,
    bool Enabled,
    bool Deleted,
    bool InProject,
    bool HasActions,
    string? WorkerName,
    string WorkerCli,
    string WorkerPrompt,
    IReadOnlyList<string> ScriptPaths,
    string? ActiveRunId,
    bool ActiveRunIsRunning);

/// <summary>A lane entry recorded for a card that the scheduler has not consumed yet.</summary>
public sealed record BoardPendingLaneAutomation(long JobId, string ColumnId, DateTime DueUtc);

public partial interface IBoardStore
{
    Task<BoardLaneAutomation?> GetLaneAutomationAsync(string projectPath, string columnId, CancellationToken cancellationToken = default);
    Task<BoardLaneAutomation?> SaveLaneAutomationAsync(string projectPath, string columnId, IReadOnlyList<long> jobIds, int expectedRevision, CancellationToken cancellationToken = default);
    /// <summary>Reads a bounded batch without claiming or removing entries.</summary>
    Task<IReadOnlyList<BoardLaneAutomationEvent>> GetDueLaneAutomationsAsync(DateTime nowUtc, CancellationToken cancellationToken = default);
    /// <summary>Removes only the observed entry; a subsequent lane entry must survive.</summary>
    Task AcknowledgeLaneAutomationAsync(BoardLaneAutomationEvent entry, CancellationToken cancellationToken = default);

    /// <summary>
    /// Describes selected Automations from their local definitions, one entry per requested id in
    /// the requested order. Never throws for a missing definition; see <see cref="BoardLaneAutomationDefinition"/>.
    /// </summary>
    Task<IReadOnlyList<BoardLaneAutomationDefinition>> DescribeLaneAutomationsAsync(string projectPath, IReadOnlyList<long> jobIds, CancellationToken cancellationToken = default);
    /// <summary>The card's lane entries that have not settled yet, soonest first.</summary>
    Task<IReadOnlyList<BoardPendingLaneAutomation>> GetPendingLaneAutomationsAsync(string projectPath, string cardId, CancellationToken cancellationToken = default);
    /// <summary>
    /// Moves a card. With <paramref name="skipLaneAutomations"/> the lane entries this move
    /// records are removed inside the same transaction, so the destination lane's Automations
    /// never queue for this entry. The move itself is unchanged.
    /// </summary>
    Task<BoardCardRecord?> MoveCardAsync(string projectPath, string cardId, string columnId, int? position, bool skipLaneAutomations, CancellationToken cancellationToken = default);
}
