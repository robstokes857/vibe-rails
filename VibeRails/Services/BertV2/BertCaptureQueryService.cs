using VibeRails.DTOs;

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

    public BertCaptureDetailResponse GetCapture(string documentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);

        var document = _searchDb.GetCapture(documentId)
            ?? throw new KeyNotFoundException($"BERT capture '{documentId}' was not found.");

        var metadata = _searchDb.GetMetadataByDocumentIds([document.DocumentId]).Values.FirstOrDefault();
        var parsedId = BertDocumentId.Parse(document.DocumentId);
        var fileChanges = parsedId.UserInputId is long userInputId
            ? _searchDb.GetFileChanges(userInputId)
            : [];

        return _responseMapper.ToCaptureDetail(document, metadata, fileChanges);
    }
}
