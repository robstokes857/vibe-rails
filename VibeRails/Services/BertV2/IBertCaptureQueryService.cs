using VibeRails.DTOs;

namespace VibeRails.Services.BertV2;

public interface IBertCaptureQueryService
{
    BertCaptureListResponse GetCaptures(int skip = 0, int take = BertSearchDefaults.DefaultTake);
    BertCaptureListResponse GetCapturesBySessionId(string sessionId);
    BertCaptureListResponse GetSessionCaptures(int skip = 0, int take = BertSearchDefaults.DefaultTake);
    BertCaptureDetailResponse GetCapture(string documentId);
}
