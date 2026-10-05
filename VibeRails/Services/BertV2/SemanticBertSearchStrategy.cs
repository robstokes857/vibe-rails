namespace VibeRails.Services.BertV2;

public sealed class SemanticBertSearchStrategy : IBertSearchStrategy
{
    private readonly Func<IBertV2BgeEmbedder> _embedder;
    private readonly IBertSearchDbService _searchDb;

    public SemanticBertSearchStrategy(IBertV2BgeEmbedder embedder, IBertSearchDbService searchDb)
        : this(() => embedder, searchDb) { }

    public SemanticBertSearchStrategy(Func<IBertV2BgeEmbedder> embedder, IBertSearchDbService searchDb)
    {
        _embedder = embedder;
        _searchDb = searchDb;
    }

    public string Mode => "semantic";
    public string Scope => BertSearchScopes.Inputs;

    public IReadOnlyList<BertStoredDocument> Search(string query, int topK)
    {
        try { return _searchDb.SearchByEmbedding(_embedder().GenerateEmbedding(query), topK); }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Semantic input search unavailable; using keywords");
            return _searchDb.SearchByText(query, topK);
        }
    }
}
