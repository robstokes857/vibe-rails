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

/// <summary>A durable dispatch observation; Waiting is retryable, all other states acknowledge the entry.</summary>
public sealed record BoardLaneAutomationDispatch(string Status, string Reason, string? RunId = null);

/// <summary>A lane entry's visible state, including entries without a terminal session.</summary>
public sealed record BoardLaneAutomationStatus(string EventKey, long JobId, string ColumnId,
    DateTime DueUtc, string Name, string Status, string Reason, string? RunId = null, string Purpose = "work",
    string? WorkflowId = null, int Position = 0, bool IsCurrent = false, string? StepStatus = null,
    bool CanSkip = false, bool RequiresVerdict = false)
{
    /// <summary>A stopped, failed current step can be explicitly rerun by the user.</summary>
    public bool CanRerun => IsCurrent && Status is not (BoardStepStatus.Queued or BoardStepStatus.Running)
        && (StepStatus ?? Status) is BoardStepStatus.Failed or BoardStepStatus.Cancelled
            or BoardStepStatus.TimedOut or BoardStepStatus.Interrupted;
}

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
    bool ActiveRunIsRunning,
    string? Description = null,
    string Purpose = "work");

/// <summary>A lane entry recorded for a card that the scheduler has not consumed yet.</summary>
public sealed record BoardPendingLaneAutomation(long JobId, string ColumnId, DateTime DueUtc);

/// <summary>A card's current lane workflow, for the lane progress display.</summary>
public sealed record BoardLaneWorkflow(string CardId, string CardLabel, IReadOnlyList<BoardLaneAutomationStatus> Steps);

/// <summary>An attributed, append-only progress or verdict receipt for one exact lane entry.</summary>
public sealed record BoardLaneStepReport(string EventKey, long JobId, string Status, string Summary,
    string? RunId = null, string? ReviewId = null);

public partial interface IBoardStore
{
    /// <summary>Current per-card workflows in one project lane, including completed steps.</summary>
    Task<IReadOnlyList<BoardLaneWorkflow>> GetLaneWorkflowsAsync(string projectPath, string columnId, CancellationToken cancellationToken = default);
    /// <summary>Why this entry must wait for an earlier step; null means eligible.</summary>
    Task<string?> GetLaneAutomationBlockReasonAsync(BoardLaneAutomationEvent entry, CancellationToken cancellationToken = default);
    /// <summary>Append a step receipt after rechecking the current entry and session membership.</summary>
    Task<bool> ReportLaneStepAsync(string projectPath, string cardId, BoardLaneStepReport report, BoardAuthor author, CancellationToken cancellationToken = default);
    /// <summary>Resolve a linked Automation Worker's immutable run context.</summary>
    Task<BoardLaneStepRun?> FindLaneStepRunAsync(string projectPath, string cardId, string sessionId, CancellationToken cancellationToken = default);
    /// <summary>Requested live cards with pending entries that have no committed run yet.</summary>
    Task<IReadOnlyList<string>> GetWaitingAutomationCardIdsAsync(string projectPath, IReadOnlyList<string> cardIds,
        CancellationToken cancellationToken = default);
    /// <summary>Skips an exact current entry and saves its comment in one Board transaction; false if stale.</summary>
    Task<bool> SkipLaneAutomationAsync(BoardLaneAutomationEvent entry, BoardAuthor author, string comment,
        CancellationToken cancellationToken = default);
    Task<BoardLaneAutomation?> GetLaneAutomationAsync(string projectPath, string columnId, CancellationToken cancellationToken = default);
    Task<BoardLaneAutomation?> SaveLaneAutomationAsync(string projectPath, string columnId, IReadOnlyList<long> jobIds, int expectedRevision, CancellationToken cancellationToken = default);
    /// <summary>Reads a bounded batch without claiming or removing entries.</summary>
    Task<IReadOnlyList<BoardLaneAutomationEvent>> GetDueLaneAutomationsAsync(DateTime nowUtc, CancellationToken cancellationToken = default);
    /// <summary>Removes only the observed entry; a subsequent lane entry must survive.</summary>
    Task AcknowledgeLaneAutomationAsync(BoardLaneAutomationEvent entry, CancellationToken cancellationToken = default);
    /// <summary>Rechecks exact entry identity immediately before the independent run commit.</summary>
    Task<bool> IsLaneAutomationCurrentAsync(BoardLaneAutomationEvent entry, CancellationToken cancellationToken = default);
    /// <summary>Retains busy demand or records an outcome and acknowledges only this entry in one Board commit.</summary>
    Task RecordLaneAutomationDispatchAsync(BoardLaneAutomationEvent entry, BoardLaneAutomationDispatch dispatch,
        DateTime nowUtc, CancellationToken cancellationToken = default);
    /// <summary>Recent entry states, with committed run status taking precedence over dispatch observations.</summary>
    Task<IReadOnlyList<BoardLaneAutomationStatus>> GetLaneAutomationStatusesAsync(string projectPath, string cardId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Describes selected Automations from their local definitions, one entry per requested id in
    /// the requested order. Never throws for a missing definition; see <see cref="BoardLaneAutomationDefinition"/>.
    /// </summary>
    Task<IReadOnlyList<BoardLaneAutomationDefinition>> DescribeLaneAutomationsAsync(string projectPath, IReadOnlyList<long> jobIds, CancellationToken cancellationToken = default);
    /// <summary>The card's uncommitted entries, including settled entries waiting for a busy Job, soonest first.</summary>
    Task<IReadOnlyList<BoardPendingLaneAutomation>> GetPendingLaneAutomationsAsync(string projectPath, string cardId, CancellationToken cancellationToken = default);
    /// <summary>
    /// Moves a card. With <paramref name="skipLaneAutomations"/> the lane entries this move
    /// records are removed inside the same transaction, so the destination lane's Automations
    /// never queue for this entry. The move itself is unchanged.
    /// </summary>
    Task<BoardCardRecord?> MoveCardAsync(string projectPath, string cardId, string columnId, int? position, bool skipLaneAutomations, CancellationToken cancellationToken = default, BoardAuthor? author = null);
}

/// <summary>Immutable run identity used to authorize a workflow result.</summary>
public sealed record BoardLaneStepRun(string Id, long JobId, string TriggerKey, DateTime QueuedUtc, string Purpose, bool Active);
