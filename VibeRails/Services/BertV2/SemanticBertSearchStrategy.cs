namespace VibeRails.Services.BertV2;

public sealed class SemanticBertSearchStrategy : IBertSearchStrategy
{
    private readonly IBertV2BgeEmbedder _embedder;
    private readonly IBertSearchDbService _searchDb;

    public SemanticBertSearchStrategy(IBertV2BgeEmbedder embedder, IBertSearchDbService searchDb)
    {
        _embedder = embedder;
        _searchDb = searchDb;
    }

    public string Mode => "semantic";

    public IReadOnlyList<BertStoredDocument> Search(string query, int topK)
    {
        var embedding = _embedder.GenerateEmbedding(query);
        return _searchDb.SearchByEmbedding(embedding, topK);
    }
}
