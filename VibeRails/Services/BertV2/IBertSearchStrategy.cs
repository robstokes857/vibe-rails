namespace VibeRails.Services.BertV2;

public interface IBertSearchStrategy
{
    string Mode { get; }
    string Scope { get; }
    IReadOnlyList<BertStoredDocument> Search(string query, int topK);
}
