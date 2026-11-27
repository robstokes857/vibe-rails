namespace VibeRails.Services.Board;

/// <summary>A local card search hit with explicit owning board and project metadata.</summary>
public sealed record BoardSearchHit(string Id, string Key, string DisplayId, string Title,
    string BoardId, string BoardName, string ColumnId, string ColumnName, string ProjectPath,
    bool IsCurrentProject, string Snippet, string MatchKind, double Score,
    string Type, string Priority, string? Assignee, bool Blocked, bool Flagged)
{
    /// <summary>The local marker used to filter this indexed search result.</summary>
    public string? GhostReason { get; init; }
}

/// <summary>Optional card filters applied before local search ranking and result limits.</summary>
public sealed record BoardSearchFilters(string? Assignee = null, string? Type = null,
    string? Priority = null, string? Origin = null, bool? Ghost = null);

/// <summary>A bounded passage of current content; its hash pins the cached model vector.</summary>
public sealed record BoardSearchPassage(string Id, string Text, string Version, float[]? Embedding);

/// <summary>Live card metadata and all current searchable passages, excluding change history.</summary>
public sealed record BoardSearchDocument(string Id, string Key, string DisplayId, string Title,
    string BoardId, string BoardName, string ColumnId, string ColumnName, string ProjectPath,
    int Number, string KeyPrefix, DateTime UpdatedUtc, string Type, string Priority,
    string? Assignee, bool Blocked, bool Flagged, IReadOnlyList<BoardSearchPassage> Passages)
{
    /// <summary>Stored creation origin, independent of current assignment.</summary>
    public bool AgentMade { get; init; }
    /// <summary>The card was first received through hosted sync.</summary>
    public bool RemoteCreated { get; init; }
    /// <summary>Whether this card has a retained Jira issue link.</summary>
    public bool HasJiraLink { get; init; }
    /// <summary>Whether this card has a retained Azure DevOps work item link.</summary>
    public bool HasAdoLink { get; init; }
    /// <summary>Local ghost annotation, carried with search metadata for filtering before ranking.</summary>
    public string? GhostReason { get; init; }
    /// <summary>Original current sources retain lexical matches longer than a semantic passage.</summary>
    public IReadOnlyList<string> KeywordSources { get; init; } = [];
    /// <summary>Full canonical source boundaries for background search ingestion.</summary>
    public IReadOnlyList<VibeRails.Services.BertV2.SearchSource> Sources { get; init; } = [];
    public IReadOnlyList<BoardSessionRecord> Sessions { get; init; } = [];
    public IReadOnlyList<BoardCommitRecord> Commits { get; init; } = [];
    public IReadOnlyList<BoardFileReference> FileCandidates { get; init; } = [];
    /// <summary>
    /// Identifies what <see cref="FileCandidates"/> were read from: the card's linked commits that have a
    /// saved snapshot, in candidate order (VIBE-327). Commit links and snapshots are insert-only, so an
    /// equal key means equal candidates and the search sweep reuses its indexed ones instead of parsing
    /// every snapshot again. Null when no linked commit has a snapshot, so there are no candidates.
    /// </summary>
    public string? CandidateKey { get; init; }
    public IReadOnlyList<string> KeyPrefixes { get; init; } = [];
    public BoardHandoff? PreviousWork { get; init; }
}

public partial interface IBoardStore
{
    /// <summary>
    /// Pages 100 live cards; an immutable ID cursor avoids offset shifts during concurrent writes.
    /// <paramref name="fileCandidates"/> false leaves <see cref="BoardSearchDocument.FileCandidates"/> empty
    /// but still sets <see cref="BoardSearchDocument.CandidateKey"/>, for a caller that keeps candidates.
    /// </summary>
    Task<IReadOnlyList<BoardSearchDocument>> GetSearchDocumentsAsync(int offset, CancellationToken cancellationToken = default,
        bool includeContent = true, string? afterCardId = null, int pageSize = 100, bool fileCandidates = true);

    /// <summary>Saves a derived vector for one live card passage; content hashes reject stale vectors.</summary>
    Task SaveSearchEmbeddingAsync(string projectPath, string cardId, string passageId, string version,
        float[] embedding, CancellationToken cancellationToken = default);
}
