namespace VibeRails.Services.BertV2;

public interface IBertSearchStrategy
{
    string Mode { get; }
    IReadOnlyList<BertStoredDocument> Search(string query, int topK);
}
