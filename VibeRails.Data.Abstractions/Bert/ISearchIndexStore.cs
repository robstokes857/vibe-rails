using VibeRails.DTOs;
using VibeRails.Services.Board;

namespace VibeRails.Services.BertV2;

/// <summary>One canonical source boundary. Full text is retained independently of model chunks.</summary>
public sealed record SearchSource(string Id, string Text, string Title = "");

/// <summary>Derived result metadata and current sources, rebuilt from the owning component.</summary>
public sealed record SearchDocument(string Id, string Kind, string ProjectPath,
    IReadOnlyList<SearchSource> Sources, BoardSearchDocument? Board = null,
    BertInputMetadata? Input = null, BertSessionMetadata? Session = null,
    IReadOnlyList<BertFileChangeResponse>? FileChanges = null);

/// <summary>A source awaiting tokenization, or a model chunk awaiting inference.</summary>
public sealed record SearchWork(string Id, string Kind, string Text, string Title, string Hash, string Lease);

/// <summary>A ranked document with the matching source/chunk, never a serialized vector.</summary>
public sealed record SearchMatch(SearchDocument Document, string Text, double Lexical, double Semantic, int Chunk = 0);

/// <summary>UI-only snapshot of durable indexing work.</summary>
public sealed record SearchIndexProgress(int Documents, int Sources, int Chunks, int Embedded,
    int PendingSources, int Failed, string? LastError, string? LastReconciledUtc);

/// <summary>The shared derived search component. Source databases are only read by reconciliation.</summary>
public interface ISearchIndexStore
{
    string DatabasePath { get; }
    /// <summary>Canonical source path for background ingestion and the existing UI diagnostics only.</summary>
    string StateDatabasePath { get; }
    Task<int> ReconcileAsync(IBoardStore board, int batchSize, CancellationToken ct);
    IReadOnlyList<SearchWork> ClaimSources(string kind, int count, string version);
    void CompleteSource(SearchWork work, IReadOnlyList<string> chunks, string version);
    IReadOnlyList<SearchWork> ClaimChunks(string kind, int count, string modelVersion);
    bool Renew(SearchWork work, bool source);
    void CompleteChunk(SearchWork work, float[] vector, string modelVersion);
    void Fail(SearchWork work, bool source, string error);
    void Release(SearchWork work, bool source);
    IReadOnlyList<SearchMatch> Search(string kind, string query, float[]? embedding, int count,
        string? projectScope = null, IReadOnlyCollection<string>? excluded = null);
    IReadOnlyList<SearchDocument> ReadDocuments(string kind, int skip = 0, int take = 100,
        IReadOnlyCollection<string>? ids = null, string? sessionId = null, long? userInputId = null);
    IReadOnlyList<SearchMatch> ReadSessionChunks(int skip, int take, string? sessionId = null, int? chunk = null);
    SearchIndexProgress GetProgress();
    int Count(string kind, bool vectors = false);
    IReadOnlySet<string> GetBoardPrefixes(string projectPath);
    BoardSearchDocument? FindSessionCard(string sessionId, string projectPath);
    bool Repair();
}
