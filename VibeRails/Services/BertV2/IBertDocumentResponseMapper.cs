using VibeRails.DTOs;

namespace VibeRails.Services.BertV2;

public interface IBertDocumentResponseMapper
{
    BertCaptureSummaryResponse ToCaptureSummary(BertStoredDocument document, BertInputMetadata? metadata);
    BertCaptureDetailResponse ToCaptureDetail(BertStoredDocument document, BertInputMetadata? metadata, IReadOnlyList<BertFileChangeResponse> fileChanges);
    BertSearchHitResponse ToSearchHit(BertStoredDocument document, BertInputMetadata? metadata);
}
