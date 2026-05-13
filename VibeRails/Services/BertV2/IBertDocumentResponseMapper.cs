using VibeRails.DTOs;

namespace VibeRails.Services.BertV2;

public interface IBertDocumentResponseMapper
{
    BertCaptureSummaryResponse ToCaptureSummary(BertStoredDocument document, BertInputMetadata? metadata);
    BertCaptureSummaryResponse ToSessionCaptureSummary(BertStoredDocument document, BertSessionMetadata? metadata);
    BertCaptureDetailResponse ToCaptureDetail(BertStoredDocument document, BertInputMetadata? metadata, IReadOnlyList<BertFileChangeResponse> fileChanges);
    BertCaptureDetailResponse ToSessionCaptureDetail(BertStoredDocument document, BertSessionMetadata? metadata);
    BertSearchHitResponse ToSearchHit(BertStoredDocument document, BertInputMetadata? metadata);
    BertSearchHitResponse ToSessionSearchHit(BertStoredDocument document, BertSessionMetadata? metadata);
}
