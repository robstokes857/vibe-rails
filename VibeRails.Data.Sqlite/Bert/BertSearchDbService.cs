using VibeRails.DTOs;
using VibeRails.Data.Sqlite;

namespace VibeRails.Services.BertV2;

/// <summary>History retrieval and result metadata from the shared search database only.</summary>
public sealed class BertSearchDbService(ISearchIndexStore store) : IBertSearchDbService
{
    public BertSearchDbService(string databasePath, string stateDatabasePath)
        : this(new SqliteSearchIndexStore(databasePath, stateDatabasePath)) { }
    public string VectorDatabasePath => store.DatabasePath;
    // Retained UI diagnostics property; no retrieval method opens this source path.
    public string StateDatabasePath => store.StateDatabasePath;
    public bool VectorDatabaseExists => File.Exists(store.DatabasePath);
    public int CountDocuments() => SqliteStorageErrors.Execute(() => store.Count("input"));
    public int CountSessions() => SqliteStorageErrors.Execute(() => store.Count("history-sessions"));
    public int CountVectors() => SqliteStorageErrors.Execute(() => store.Count("input", true));
    public int CountSessionDocuments() => SqliteStorageErrors.Execute(() => store.Count("session-chunks"));
    public int CountSessionVectors() => SqliteStorageErrors.Execute(() => store.Count("session", true));
    public string? GetLatestDocumentId() => store.ReadDocuments("input", take: 1).FirstOrDefault()?.Id;
    public IReadOnlyList<BertStoredDocument> GetCaptures(int skip, int take) => Captures("input", skip, take);
    public IReadOnlyList<BertStoredDocument> GetCapturesBySessionId(string sessionId) => store.ReadDocuments("input", take: 10000, sessionId: sessionId).Select(Stored).ToArray();
    public IReadOnlyList<BertStoredDocument> GetSessionRecallPage(string sessionId, int offset, int take) =>
        store.ReadDocuments("input", offset, Math.Clamp(take, 1, 20), sessionId: sessionId)
            .Select(doc => Stored(doc) with { RawText = Text(doc)[..Math.Min(1000, Text(doc).Length)] }).ToArray();
    public IReadOnlyList<BertStoredDocument> GetSessionCaptures(int skip, int take) => store.ReadSessionChunks(skip, take)
        .Select(match => new BertStoredDocument(BertSessionDocumentId.Create(match.Document.Session!.SessionId, match.Chunk), match.Text, null)).ToArray();
    public BertStoredDocument? GetCapture(string documentId) => store.ReadDocuments("input", ids: [documentId]).Select(Stored).FirstOrDefault();
    public BertStoredDocument? GetSessionCapture(string documentId)
    {
        if (BertSessionDocumentId.Parse(documentId) is not { } parsed) return null;
        var chunk = store.ReadSessionChunks(0, 1, parsed.SessionId, parsed.ChunkIndex).FirstOrDefault();
        if (chunk is not null) return new(documentId, chunk.Text, null);
        // Lexical ingestion works before a tokenizer is available. In that window the
        // source is the sole session document, and its follow-up must still be readable.
        return parsed.ChunkIndex == 0
            ? store.ReadDocuments("session", take: 1, sessionId: parsed.SessionId)
                .Select(doc => new BertStoredDocument(documentId, Text(doc), null)).FirstOrDefault()
            : null;
    }
    public IReadOnlyList<BertStoredDocument> SearchByText(string query, int topK) => Search("input", query, null, topK);
    public IReadOnlyList<BertStoredDocument> SearchByEmbedding(float[] embedding, int topK) => Search("input", "", embedding, topK);
    public IReadOnlyList<BertStoredDocument> SearchSessionsByText(string query, int topK) => Search("session", query, null, topK);
    public IReadOnlyList<BertStoredDocument> SearchSessionsByEmbedding(float[] embedding, int topK) => Search("session", "", embedding, topK);

    public IReadOnlyDictionary<string, BertInputMetadata> GetMetadataByDocumentIds(IReadOnlyCollection<string> ids) =>
        ids.Count == 0 ? new Dictionary<string, BertInputMetadata>() : store.ReadDocuments("input", ids: ids, take: ids.Count)
            .Where(doc => doc.Input is not null).ToDictionary(doc => doc.Id, doc => doc.Input!, StringComparer.Ordinal);

    public IReadOnlyDictionary<string, BertSessionMetadata> GetSessionMetadataByDocumentIds(IReadOnlyCollection<string> ids)
    {
        if (ids.Count == 0) return new Dictionary<string, BertSessionMetadata>();
        var docs = store.ReadDocuments("session", ids: ids.Select(SessionOwner).Distinct().ToArray(), take: ids.Count)
            .Where(doc => doc.Session is not null).ToDictionary(doc => doc.Id, StringComparer.Ordinal);
        var result = new Dictionary<string, BertSessionMetadata>(StringComparer.Ordinal);
        foreach (var id in ids)
            if (docs.TryGetValue(SessionOwner(id), out var doc))
                result[id] = doc.Session! with { DocumentId = id, ChunkIndex = BertSessionDocumentId.Parse(id)?.ChunkIndex ?? 0 };
        return result;
    }

    public IReadOnlyList<BertFileChangeResponse> GetFileChanges(long userInputId) =>
        store.ReadDocuments("input", userInputId: userInputId).FirstOrDefault()?.FileChanges ?? [];
    public IReadOnlyList<BertFileChangeResponse> GetSessionFileChanges(string sessionId) =>
        store.ReadDocuments("session", sessionId: sessionId).FirstOrDefault()?.FileChanges ?? [];

    private IReadOnlyList<BertStoredDocument> Captures(string kind, int skip, int take) => store.ReadDocuments(kind, skip, take).Select(Stored).ToArray();
    private static string Text(SearchDocument doc) => string.Join("\n\n", doc.Sources.Select(source => source.Text));
    private static BertStoredDocument Stored(SearchDocument doc) => new(doc.Id, Text(doc), null);
    private static string SessionOwner(string id) => BertSessionDocumentId.Parse(id) is { } parsed ? BertSessionDocumentId.Create(parsed.SessionId, 0) : id;
    private IReadOnlyList<BertStoredDocument> Search(string kind, string query, float[]? vector, int count)
    {
        if (vector is null && string.IsNullOrWhiteSpace(query)) return [];
        return store.Search(kind, query, vector, Math.Clamp(count, 1, 50))
            .OrderByDescending(match => vector is null ? match.Lexical : match.Semantic).Take(Math.Clamp(count, 1, 50))
            .Select(match => new BertStoredDocument(kind == "session"
                ? BertSessionDocumentId.Create(match.Document.Session!.SessionId, match.Chunk) : match.Document.Id,
                match.Text, vector is null ? null : match.Semantic)).ToArray();
    }
}
