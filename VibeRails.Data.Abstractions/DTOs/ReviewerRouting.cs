using VibeRails.Services.Board;

namespace VibeRails.DTOs;

/// <summary>A reviewer selection in the shared LLM picker. Options apply only to base providers.</summary>
public sealed record ReviewerTarget(string Selection, BaseLlmOptions? Options = null);

/// <summary>An explicit provider mapping; additional providers need no assumed opposite.</summary>
public sealed record ReviewerMapping(string SourceProvider, ReviewerTarget Reviewer);

/// <summary>Editable reviewer selection on an existing Code review Worker.</summary>
public sealed record ReviewerRouting(string Mode, IReadOnlyList<ReviewerMapping> Mappings, ReviewerTarget Fallback)
{
    /// <summary>Defaults for new Switch reviewer presets, never a reclassification of old Workers.</summary>
    public static ReviewerRouting SwitchDefault() => new("switch",
        [new("claude", new("base:codex")), new("codex", new("base:claude"))], new("base:codex"));
}

/// <summary>Explicit attribution and requested scope for a card. No path comes from the client.</summary>
public sealed record BoardReviewSettings(string SourceKind = "unknown", string? SourceSessionId = null,
    string Description = "", string Scope = "working-tree", string? BaseCommit = null, string? HeadCommit = null,
    bool IncludeDirty = false, ReviewerRouting? Routing = null);

/// <summary>The declared coding source, including the evidence used and its original checkout.</summary>
public sealed record ReviewCodingSource(string Kind, string? SessionId, string? Provider, string Description,
    string? Workspace = null);

/// <summary>Immutable public routing evidence attached to the queued run and canonical report.</summary>
public sealed record ReviewRoutingSnapshot(ReviewCodingSource Source, ReviewerRouting Routing,
    ReviewerTarget Selected, string Provider, string Reviewer, bool UsedFallback, bool Overridden,
    string Workspace, string Scope, string ScopeDescription, string? BaseCommit, string? HeadCommit,
    bool IncludeDirty, string? InputHash, string? Limitations, string? Problem = null, string? CardKey = null, string? Model = null);

/// <summary>Private launch prerequisites. Environment edits require a new review, never silent rerouting.</summary>
public sealed record ReviewLaunchSnapshot(ReviewRoutingSnapshot Resolution, int WorkerId = 0,
    string? WorkerPrompt = null, string? EnvironmentFingerprint = null, string? WorkerProblem = null);

/// <summary>Prepare review routing before the state database queue transaction begins.</summary>
public interface IReviewRunSnapshotFactory
{
    Task<ReviewLaunchSnapshot?> PrepareAsync(string project, int workerId, string? cardKey, CancellationToken cancellationToken,
        string defaultScope = "working-tree");
}
