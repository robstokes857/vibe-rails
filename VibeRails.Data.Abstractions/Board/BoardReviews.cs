namespace VibeRails.Services.Board;

/// <summary>An explicit review attempt and its immutable report. Process outcome is separate.</summary>
public sealed record BoardReviewRecord(
    string Id, string CardId, string Provider, string Reviewer, DateTime CreatedUtc,
    string? RunId = null, string? SessionId = null, string? TabId = null,
    string ProcessStatus = "Queued", string? Error = null,
    string? Workspace = null, string? Scope = null, string? ScopeDescription = null,
    string? BaseCommit = null, string? HeadCommit = null, bool IncludeDirty = false,
    string? SnapshotHash = null, string? CaptureLimitations = null,
    DateTime? CapturedUtc = null, string? Result = null, string? Findings = null,
    string? Validation = null, string? Limitations = null, DateTime? ReportedUtc = null,
    string Freshness = "Unknown", string? TerminalSessionId = null, IReadOnlyList<string>? ScopeFiles = null,
    VibeRails.DTOs.ReviewRoutingSnapshot? Routing = null);

public partial interface IBoardStore
{
    /// <summary>Read explicit immutable launch/run purpose for a linked session; never infer from names.</summary>
    Task<string?> FindSessionPurposeAsync(string projectPath, string cardId, string sessionId, CancellationToken cancellationToken = default);
    /// <summary>Known discussion/planning/review provenance on any linked card in this project.</summary>
    Task<bool> IsNonCodingSessionAsync(string projectPath, string sessionId, CancellationToken cancellationToken = default);
    /// <summary>Explicit per-card coding source and scope; absent settings mean unknown source.</summary>
    Task<VibeRails.DTOs.BoardReviewSettings?> GetReviewSettingsAsync(string projectPath, string cardId, CancellationToken cancellationToken = default);
    /// <summary>Save review inputs on a live project-scoped card without rewriting run history.</summary>
    Task<bool> SaveReviewSettingsAsync(string projectPath, string cardId, VibeRails.DTOs.BoardReviewSettings settings, CancellationToken cancellationToken = default);
    /// <summary>Find an owning session's retained review attempt without a history page limit.</summary>
    Task<BoardReviewRecord?> GetReviewForSessionAsync(string projectPath, string cardId, string sessionId, CancellationToken cancellationToken = default);
    /// <summary>Find an explicit review run by its run id or owning Worker session.</summary>
    Task<BoardAgentRun?> FindReviewRunAsync(string projectPath, string cardId, string? runId, string? sessionId, CancellationToken cancellationToken = default);
    /// <summary>Explicit review runs, including attempts without a recording, in bounded pages.</summary>
    Task<IReadOnlyList<BoardAgentRun>> GetReviewRunsAsync(string projectPath, string cardId, int offset = 0, CancellationToken cancellationToken = default);
    /// <summary>Classify only runs whose immutable snapshot explicitly says Code review.</summary>
    Task<IReadOnlySet<string>> GetReviewSessionIdsAsync(string projectPath, IReadOnlyList<string> sessionIds, CancellationToken cancellationToken = default);
    /// <summary>Save a new attempt or update an unfinished one, preserving its launch identity.</summary>
    Task<bool> SaveReviewAsync(string projectPath, BoardReviewRecord review, CancellationToken cancellationToken = default);
    /// <summary>Read retained review attempts, newest first, through the card/project boundary.</summary>
    Task<IReadOnlyList<BoardReviewRecord>> GetReviewsAsync(string projectPath, string cardId, int offset = 0, CancellationToken cancellationToken = default);
    /// <summary>Latest saved report, independent of attempt history pagination.</summary>
    Task<BoardReviewRecord?> GetLatestReviewAsync(string projectPath, string cardId, CancellationToken cancellationToken = default);
    /// <summary>Read one canonical report/attempt on the scoped card.</summary>
    Task<BoardReviewRecord?> GetReviewAsync(string projectPath, string cardId, string reviewId, CancellationToken cancellationToken = default);
}
