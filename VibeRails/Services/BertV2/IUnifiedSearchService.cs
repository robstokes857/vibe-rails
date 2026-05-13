using VibeRails.DTOs;

namespace VibeRails.Services.BertV2;

public interface IUnifiedSearchService
{
    UnifiedSearchResponse Search(string query, int topK);
}
