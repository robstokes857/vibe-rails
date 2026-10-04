namespace VibeRails.Services.Board;

/// <summary>A local card search hit with explicit owning board and project metadata.</summary>
public sealed record BoardSearchHit(string Id, string Key, string DisplayId, string Title,
    string BoardId, string BoardName, string ColumnId, string ColumnName, string ProjectPath,
    bool IsCurrentProject, string Snippet, string MatchKind, double Score,
    string Type, string Priority, string? Assignee, bool Blocked, bool Flagged);

/// <summary>A bounded passage of current content; its hash pins the cached model vector.</summary>
public sealed record BoardSearchPassage(string Id, string Text, string Version, float[]? Embedding);

/// <summary>Live card metadata and all current searchable passages, excluding change history.</summary>
public sealed record BoardSearchDocument(string Id, string Key, string DisplayId, string Title,
    string BoardId, string BoardName, string ColumnId, string ColumnName, string ProjectPath,
    int Number, string KeyPrefix, DateTime UpdatedUtc, string Type, string Priority,
    string? Assignee, bool Blocked, bool Flagged, IReadOnlyList<BoardSearchPassage> Passages)
{
    /// <summary>Original current sources retain lexical matches longer than a semantic passage.</summary>
    public IReadOnlyList<string> KeywordSources { get; init; } = [];
}

public partial interface IBoardStore
{
    /// <summary>Pages 100 live cards; an immutable ID cursor avoids offset shifts during concurrent writes.</summary>
    Task<IReadOnlyList<BoardSearchDocument>> GetSearchDocumentsAsync(int offset, CancellationToken cancellationToken = default,
        bool includeContent = true, string? afterCardId = null);

    /// <summary>Saves a derived vector for one live card passage; content hashes reject stale vectors.</summary>
    Task SaveSearchEmbeddingAsync(string projectPath, string cardId, string passageId, string version,
        float[] embedding, CancellationToken cancellationToken = default);
}
