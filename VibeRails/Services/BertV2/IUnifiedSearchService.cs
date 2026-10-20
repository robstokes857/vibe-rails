using VibeRails.DTOs;

namespace VibeRails.Services.BertV2;

public interface IUnifiedSearchService
{
    /// <param name="queryEmbedding">An embedding of this query the caller already computed, so the model runs once.</param>
    UnifiedSearchResponse Search(string query, int topK, float[]? queryEmbedding = null);
}
