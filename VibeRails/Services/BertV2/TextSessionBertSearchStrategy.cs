namespace VibeRails.Services.BertV2;

public sealed class TextSessionBertSearchStrategy : IBertSearchStrategy
{
    private readonly IBertSearchDbService _searchDb;

    public TextSessionBertSearchStrategy(IBertSearchDbService searchDb)
    {
        _searchDb = searchDb;
    }

    public string Mode => "text";
    public string Scope => BertSearchScopes.Sessions;

    public IReadOnlyList<BertStoredDocument> Search(string query, int topK)
    {
        return _searchDb.SearchSessionsByText(query, topK);
    }
}
