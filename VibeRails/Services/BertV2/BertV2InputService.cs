

using VibeRails.Services.BertBaseClasses;

namespace VibeRails.Services.BertV2;

public class BertV2InputService : BertBase, IBertV2InputService
{
    private readonly BertV2BgeEmbedder _embedder;
    private readonly BertV2VectorStore _store;
    private readonly Lock _writeLock = new();

    public BertV2InputService(IBertSettings settings)
        : base(settings)
    {
        _embedder = new BertV2BgeEmbedder(ModelPath, VocabPath);
        _store = new BertV2VectorStore(Path.Combine(DataDirectory, "vectors.db"));
    }

    public void Capture(string sessionId, long userInputId, string inputText)
    {
        var embedding = _embedder.GenerateEmbedding(inputText);

        lock (_writeLock)
        {
            _store.AddOrUpdate($"{sessionId}:{userInputId}", inputText, embedding);
        }
    }

    public List<SearchResult> Search(string query, int topK = 10)
    {
        var embedding = _embedder.GenerateEmbedding(query);
        return _store.Search(embedding, topK);
    }

    public void Dispose()
    {
        _embedder.Dispose();
        _store.Dispose();
    }
}
