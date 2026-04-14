namespace VibeRails.Services.BertV2;

public sealed class TextBertSearchStrategy : IBertSearchStrategy
{
    private readonly IBertSearchDbService _searchDb;

    public TextBertSearchStrategy(IBertSearchDbService searchDb)
    {
        _searchDb = searchDb;
    }

    public string Mode => "text";

    public IReadOnlyList<BertStoredDocument> Search(string query, int topK)
    {
        return _searchDb.SearchByText(query, topK);
    }
}
