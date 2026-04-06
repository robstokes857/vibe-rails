namespace VibeRails.Services.BertV2;

public interface IBertV2InputService : IDisposable
{
    void Capture(string sessionId, long userInputId, string inputText);
    List<SearchResult> Search(string query, int topK = 10);
}
