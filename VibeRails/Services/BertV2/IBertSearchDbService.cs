using VibeRails.DTOs;

namespace VibeRails.Services.BertV2;

public interface IBertSearchDbService
{
    string VectorDatabasePath { get; }
    string StateDatabasePath { get; }
    bool VectorDatabaseExists { get; }

    int CountDocuments();
    int CountSessions();
    int CountVectors();
    string? GetLatestDocumentId();
    IReadOnlyList<BertStoredDocument> GetCaptures(int skip, int take);
    IReadOnlyList<BertStoredDocument> GetCapturesBySessionId(string sessionId);
    BertStoredDocument? GetCapture(string documentId);
    IReadOnlyList<BertStoredDocument> SearchByText(string query, int topK);
    IReadOnlyList<BertStoredDocument> SearchByEmbedding(float[] embedding, int topK);
    IReadOnlyDictionary<string, BertInputMetadata> GetMetadataByDocumentIds(IReadOnlyCollection<string> documentIds);
    IReadOnlyList<BertFileChangeResponse> GetFileChanges(long userInputId);
}
