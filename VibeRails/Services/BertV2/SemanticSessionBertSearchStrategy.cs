namespace VibeRails.Services.BertV2;

public sealed class SemanticSessionBertSearchStrategy : IBertSearchStrategy
{
    private readonly IBertV2BgeEmbedder _embedder;
    private readonly IBertSearchDbService _searchDb;

    public SemanticSessionBertSearchStrategy(IBertV2BgeEmbedder embedder, IBertSearchDbService searchDb)
    {
        _embedder = embedder;
        _searchDb = searchDb;
    }

    public string Mode => "semantic";
    public string Scope => BertSearchScopes.Sessions;

    public IReadOnlyList<BertStoredDocument> Search(string query, int topK)
    {
        var embedding = _embedder.GenerateEmbedding(query);
        return _searchDb.SearchSessionsByEmbedding(embedding, topK);
    }
}
