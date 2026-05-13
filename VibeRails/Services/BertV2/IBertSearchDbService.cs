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
    int CountSessionDocuments();
    int CountSessionVectors();
    string? GetLatestDocumentId();
    IReadOnlyList<BertStoredDocument> GetCaptures(int skip, int take);
    IReadOnlyList<BertStoredDocument> GetCapturesBySessionId(string sessionId);
    IReadOnlyList<BertStoredDocument> GetSessionCaptures(int skip, int take);
    BertStoredDocument? GetCapture(string documentId);
    BertStoredDocument? GetSessionCapture(string documentId);
    IReadOnlyList<BertStoredDocument> SearchByText(string query, int topK);
    IReadOnlyList<BertStoredDocument> SearchByEmbedding(float[] embedding, int topK);
    IReadOnlyList<BertStoredDocument> SearchSessionsByText(string query, int topK);
    IReadOnlyList<BertStoredDocument> SearchSessionsByEmbedding(float[] embedding, int topK);
    IReadOnlyDictionary<string, BertInputMetadata> GetMetadataByDocumentIds(IReadOnlyCollection<string> documentIds);
    IReadOnlyDictionary<string, BertSessionMetadata> GetSessionMetadataByDocumentIds(IReadOnlyCollection<string> documentIds);
    IReadOnlyList<BertFileChangeResponse> GetFileChanges(long userInputId);
    IReadOnlyList<BertFileChangeResponse> GetSessionFileChanges(string sessionId);
}
