using VibeRails.DTOs;

namespace VibeRails.Services.Board;

/// <summary>An agent's final report, independent of its process or Automation run outcome.</summary>
public sealed record BoardAgentCompletion(string SessionId, string Outcome, string Summary, DateTime CompletedUtc);

/// <summary>Bounded polling context for one linked agent; it excludes card bodies and historical activity.</summary>
public sealed record BoardAgentSessionStatus(BoardSessionRecord Session, BoardAgentCompletion? Completion,
    string? LastUpdate, DateTime? UpdatedUtc, bool Active = false);

/// <summary>A card-triggered Automation, including runs that have not opened a terminal yet.</summary>
public sealed record BoardAgentRun(string Id, string Name, JobRunStatus Status, DateTime QueuedUtc,
    string? SessionId, string? WorkerSessionId, string? Error, string Purpose = "work", string Provider = "unknown", string? Reviewer = null, ReviewRoutingSnapshot? Routing = null);

public partial interface IBoardStore
{
    /// <summary>The newest ten linked sessions, or one requested session, with bounded latest updates.</summary>
    Task<IReadOnlyList<BoardAgentSessionStatus>> GetAgentSessionsAsync(string projectPath, string cardId,
        string? sessionId = null, CancellationToken cancellationToken = default);
    /// <summary>Records the first completion report for a session linked to this card and project.</summary>
    Task<BoardAgentCompletion?> CompleteAgentAsync(string projectPath, string cardId, string sessionId,
        string outcome, string summary, CancellationToken cancellationToken = default);

    /// <summary>Reads reports only for sessions currently linked to the scoped card.</summary>
    Task<IReadOnlyList<BoardAgentCompletion>> GetAgentCompletionsAsync(string projectPath, string cardId,
        CancellationToken cancellationToken = default);

    /// <summary>Reads recent card-triggered runs, without requiring a Jobs host or creating state tables.</summary>
    Task<IReadOnlyList<BoardAgentRun>> GetAgentRunsAsync(string projectPath, string cardId,
        CancellationToken cancellationToken = default);
}
