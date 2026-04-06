namespace VibeRails.Services.BertV2;

public interface IBertV2VectorStore : IDisposable
{
    void AddOrUpdate(string id, string text, float[] embedding);
    List<SearchResult> Search(float[] queryEmbedding, int topK);
}
