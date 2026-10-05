namespace VibeRails.Services.BertV2;

public sealed class SemanticSessionBertSearchStrategy : IBertSearchStrategy
{
    private readonly Func<IBertV2BgeEmbedder> _embedder;
    private readonly IBertSearchDbService _searchDb;

    public SemanticSessionBertSearchStrategy(IBertV2BgeEmbedder embedder, IBertSearchDbService searchDb)
        : this(() => embedder, searchDb) { }

    public SemanticSessionBertSearchStrategy(Func<IBertV2BgeEmbedder> embedder, IBertSearchDbService searchDb)
    {
        _embedder = embedder;
        _searchDb = searchDb;
    }

    public string Mode => "semantic";
    public string Scope => BertSearchScopes.Sessions;

    public IReadOnlyList<BertStoredDocument> Search(string query, int topK)
    {
        try { return _searchDb.SearchSessionsByEmbedding(_embedder().GenerateEmbedding(query), topK); }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Semantic session search unavailable; using keywords");
            return _searchDb.SearchSessionsByText(query, topK);
        }
    }
}
