using VibeRails.DTOs;

namespace VibeRails.Services.Bert;

public interface IBertExplorerService
{
    BertStatusResponse GetStatus();
    BertCaptureListResponse GetCaptures(int skip = 0, int take = 50);
    BertCaptureDetailResponse? GetCapture(string documentId);
    BertSearchResponse Search(string query, string mode, int topK);
}
