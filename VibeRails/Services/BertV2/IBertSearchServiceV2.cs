using VibeRails.DTOs;

namespace VibeRails.Services.BertV2;

public interface IBertSearchServiceV2
{
    BertSearchResponse Search(string query, string mode, int topK);
    BertSearchResponse Search(string query, string mode, string scope, int topK);
}
