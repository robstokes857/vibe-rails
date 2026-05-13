using VibeRails.DTOs;
using VibeRails.Services.UserInOut;

namespace VibeRails.Services.BertV2;

public sealed class BertCaptureQueryService : IBertCaptureQueryService
{
    private readonly IBertSearchDbService _searchDb;
    private readonly IBertDocumentResponseMapper _responseMapper;

    public BertCaptureQueryService(IBertSearchDbService searchDb, IBertDocumentResponseMapper responseMapper)
    {
        _searchDb = searchDb;
        _responseMapper = responseMapper;
    }

    public BertCaptureListResponse GetCaptures(int skip = 0, int take = BertSearchDefaults.DefaultTake)
    {
        var normalizedSkip = Math.Max(0, skip);
        var normalizedTake = Math.Clamp(take <= 0 ? BertSearchDefaults.DefaultTake : take, 1, BertSearchDefaults.MaxTake);

        var documents = _searchDb.GetCaptures(normalizedSkip, normalizedTake);
        var metadataByDocumentId = _searchDb.GetMetadataByDocumentIds(documents.Select(static doc => doc.DocumentId).ToArray());
        var captures = documents
            .Select(doc => _responseMapper.ToCaptureSummary(doc, metadataByDocumentId.GetValueOrDefault(doc.DocumentId)))
            .ToList();

        return new BertCaptureListResponse(
            captures,
            _searchDb.CountDocuments(),
            normalizedSkip,
            normalizedTake);
    }

    public BertCaptureListResponse GetCapturesBySessionId(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var documents = _searchDb.GetCapturesBySessionId(sessionId);
        var metadataByDocumentId = _searchDb.GetMetadataByDocumentIds(documents.Select(static doc => doc.DocumentId).ToArray());
        var captures = documents
            .Select(doc => _responseMapper.ToCaptureSummary(doc, metadataByDocumentId.GetValueOrDefault(doc.DocumentId)))
            .ToList();

        return new BertCaptureListResponse(captures, captures.Count, 0, captures.Count);
    }

    public BertCaptureListResponse GetSessionCaptures(int skip = 0, int take = BertSearchDefaults.DefaultTake)
    {
        var normalizedSkip = Math.Max(0, skip);
        var normalizedTake = Math.Clamp(take <= 0 ? BertSearchDefaults.DefaultTake : take, 1, BertSearchDefaults.MaxTake);

        var documents = _searchDb.GetSessionCaptures(normalizedSkip, normalizedTake);
        var metadataByDocumentId = _searchDb.GetSessionMetadataByDocumentIds(documents.Select(static doc => doc.DocumentId).ToArray());
        var captures = documents
            .Select(doc => _responseMapper.ToSessionCaptureSummary(doc, metadataByDocumentId.GetValueOrDefault(doc.DocumentId)))
            .ToList();

        return new BertCaptureListResponse(
            captures,
            _searchDb.CountSessionDocuments(),
            normalizedSkip,
            normalizedTake);
    }

    public BertCaptureDetailResponse GetCapture(string documentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);

        // Session-chunk ids carry the "sess:" prefix; route those to the session store.
        if (BertSessionDocumentId.Parse(documentId) is not null)
        {
            var sessionDoc = _searchDb.GetSessionCapture(documentId)
                ?? throw new KeyNotFoundException($"BERT session capture '{documentId}' was not found.");
            var sessionMeta = _searchDb.GetSessionMetadataByDocumentIds([sessionDoc.DocumentId]).Values.FirstOrDefault();
            return _responseMapper.ToSessionCaptureDetail(sessionDoc, sessionMeta);
        }

        // Lexical (FTS5) hits are produced from state.db UserInputs, which may not
        // yet have been picked up by the periodic BertEmbeddingBackfillJob — in that
        // window the vector document table has no row for the documentId, and a
        // strict lookup would 404 the click. Fall back to synthesizing a document
        // straight from state.db so the detail view still renders.
        var document = _searchDb.GetCapture(documentId);
        var metadata = _searchDb.GetMetadataByDocumentIds([documentId])?.GetValueOrDefault(documentId);
        if (document is null)
        {
            if (metadata is null)
                throw new KeyNotFoundException($"BERT capture '{documentId}' was not found.");

            // Pass the raw input through the same ETL filter the embedding pipeline
            // uses. Rows the filter rejects (secrets, noise) must not leak back via
            // a direct doc-id lookup — even with no vector row, an attacker could
            // enumerate <sessionId>:<userInputId> to fetch filtered content.
            var filtered = InputEtlFilter.Process(metadata.InputText);
            if (filtered is null)
                throw new KeyNotFoundException($"BERT capture '{documentId}' was not found.");

            document = new BertStoredDocument(documentId, filtered, Score: null);
        }

        var parsedId = BertDocumentId.Parse(document.DocumentId);
        var fileChanges = parsedId.UserInputId is long userInputId
            ? _searchDb.GetFileChanges(userInputId)
            : [];

        return _responseMapper.ToCaptureDetail(document, metadata, fileChanges);
    }
}
