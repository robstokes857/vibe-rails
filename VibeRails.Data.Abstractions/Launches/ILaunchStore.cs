namespace VibeRails.Launches;

/// <summary>Persistence boundary for the global launch catalog and its execution ledger.</summary>
public interface ILaunchStore
{
    Task<IReadOnlyList<LaunchPreset>> ListPresetsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<LaunchAction>> ListActionsAsync(CancellationToken ct = default);
    Task<LaunchPreset?> GetPresetAsync(string id, CancellationToken ct = default);
    Task<LaunchAction?> GetActionAsync(string id, CancellationToken ct = default);
    Task<LaunchPreset> SavePresetAsync(LaunchPreset preset, long expectedRevision, CancellationToken ct = default);
    Task<LaunchAction> SaveActionAsync(LaunchAction action, long expectedRevision, CancellationToken ct = default);
    Task<LaunchAction> SaveApprovedActionAsync(LaunchAction action, long expectedRevision, string project, string? hash, CancellationToken ct = default);
    Task<IReadOnlyList<LaunchTriggerBinding>> ListBindingsAsync(string? projectPath = null, string? definitionId = null, CancellationToken ct = default);
    Task<LaunchTriggerBinding> SaveBindingAsync(LaunchTriggerBinding binding, long expectedRevision, CancellationToken ct = default);
    Task<bool> AdvanceBindingAsync(string id, long revision, DateTime occurrenceUtc, DateTime nextUtc, CancellationToken ct = default);
    Task ApproveScriptAsync(string projectPath, string definitionId, string hash, CancellationToken ct = default);
    Task<bool> IsScriptApprovedAsync(string projectPath, string definitionId, string hash, CancellationToken ct = default);
    Task<LaunchRun> EnqueueAsync(LaunchSnapshot snapshot, string? sourceRunId = null, CancellationToken ct = default);
    Task<LaunchRun?> GetRunAsync(string id, CancellationToken ct = default);
    Task<LaunchRun?> FindOccurrenceAsync(string project, string occurrence, CancellationToken ct = default);
    Task<LaunchRunPage> ListRunsAsync(string projectPath, string? definitionId = null, long? before = null, int limit = 50, CancellationToken ct = default);
    Task<IReadOnlyList<LaunchRun>> GetActiveRunsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<LaunchRun>> GetBoardRunsAsync(string project, string cardId, int offset = 0, string? runId = null, string? sessionId = null, CancellationToken ct = default);
    Task<bool> RecordVerdictAsync(LaunchClaim claim, string verdict, string? evidenceId, CancellationToken ct = default);
    Task<bool> RecordProcessAsync(LaunchClaim claim, int pid, DateTime startedUtc, CancellationToken ct = default);
    Task<LaunchClaim?> TryClaimAsync(string id, int capacity, int ownerPid, DateTime ownerStartedUtc, CancellationToken ct = default);
    Task<bool> SetTabAsync(LaunchClaim claim, string tabId, CancellationToken ct = default);
    Task<bool> StartAsync(LaunchClaim claim, string? sessionId, string? prompt, string workingDirectory,
        int ownerPid, DateTime ownerStartedUtc, DateTime? deadlineUtc = null, CancellationToken ct = default);
    /// <summary>Seals a failed handoff only if no execution has won the durable start.</summary>
    Task<bool> CompleteUnstartedAsync(LaunchClaim claim, string status, string? error = null, CancellationToken ct = default);
    Task<bool> CompleteAsync(LaunchClaim claim, string status, int? exitCode = null, string? error = null,
        string? evidenceId = null, string? verdict = null, CancellationToken ct = default);
    Task<bool> CancelAsync(string id, string projectPath, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetOpenProjectRootsAsync(DateTime nowUtc, CancellationToken ct = default);
    Task<bool> TryLeaseAsync(string name, string owner, DateTime nowUtc, TimeSpan duration, CancellationToken ct = default);
    /// <summary>
    /// The <c>project:&lt;key&gt;</c> presence lease, recording the project as this root spells it so
    /// <see cref="GetOpenProjectRootsAsync"/> returns a usable path rather than the comparison key.
    /// </summary>
    Task<bool> TryLeaseProjectAsync(string projectKey, string projectRoot, string owner, DateTime nowUtc, TimeSpan duration, CancellationToken ct = default);
    Task<IReadOnlyList<LaunchAttachment>> ListAttachmentsAsync(string definitionId, CancellationToken ct = default);
    Task<LaunchAttachment> AddAttachmentAsync(string definitionId, string name, string mimeType, byte[] content, CancellationToken ct = default);
    Task<LaunchAttachmentContent?> ReadAttachmentAsync(string definitionId, string attachmentId, CancellationToken ct = default);
    Task<LaunchAttachmentContent?> ReadRunAttachmentAsync(string project, string runId, string attachmentId, CancellationToken ct = default);
    Task<bool> RemoveAttachmentAsync(string definitionId, string attachmentId, CancellationToken ct = default);
    Task SaveCheckEvidenceAsync(string runId, VibeRails.Services.Board.BoardCheckRecord check, CancellationToken ct = default);
    Task<VibeRails.Services.Board.BoardCheckRecord?> GetCheckEvidenceAsync(string runId, CancellationToken ct = default);
}
