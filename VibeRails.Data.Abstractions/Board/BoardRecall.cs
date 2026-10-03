namespace VibeRails.Services.Board;

/// <summary>A curated repository entry point. Paths are relative; contents are never embedded.</summary>
public sealed record BoardFileReference(string Path, string Reason, string Role = "implementation", string? Symbol = null, string? Commit = null)
{
    public string? Status { get; init; }
}

/// <summary>A concise account of completed work, independent of the user's task description.</summary>
public sealed record BoardHandoff(string Outcome, string Decisions, string Validation, string Outstanding,
    IReadOnlyList<BoardFileReference> Files)
{
    public string? Id { get; init; }
    public BoardAuthor? Author { get; init; }
    public DateTime? CreatedUtc { get; init; }
}

/// <summary>Bounded current card text used by recall. Historical descriptions are not indexed.</summary>
public sealed record BoardRecallDocument(string CardId, string Key, string DisplayId, string Title,
    string Text, string Version, float[]? Embedding = null);

public partial interface IBoardStore
{
    /// <summary>Permanent, legacy and current/historical display prefixes recognized in this project.</summary>
    Task<IReadOnlySet<string>> GetRecallKeyPrefixesAsync(string projectPath, CancellationToken cancellationToken = default);
    /// <summary>Appends a handoff and a discussion receipt atomically; retains earlier handoffs.</summary>
    Task<BoardHandoff?> SaveHandoffAsync(string projectPath, string cardId, BoardHandoff handoff, BoardAuthor author, CancellationToken cancellationToken = default);
    /// <summary>Current handoff, scoped to a live card in this project.</summary>
    Task<BoardHandoff?> GetHandoffAsync(string projectPath, string cardId, CancellationToken cancellationToken = default);
    /// <summary>File names from saved commit snapshots, bounded without reading file contents.</summary>
    Task<IReadOnlyList<BoardFileReference>> GetHandoffCandidatesAsync(string projectPath, string cardId, CancellationToken cancellationToken = default);
    /// <summary>Pages current text and cached BGE vectors for recall, across this project's boards.</summary>
    Task<IReadOnlyList<BoardRecallDocument>> GetRecallDocumentsAsync(string projectPath, int offset, CancellationToken cancellationToken = default);
    /// <summary>Caches a derived vector; version checks on read prevent stale matches.</summary>
    Task SaveRecallEmbeddingAsync(string projectPath, string cardId, string version, float[] embedding, CancellationToken cancellationToken = default);
}
