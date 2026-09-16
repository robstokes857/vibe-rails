namespace VibeRails.Services.BertV2;

public interface IBertV2VectorStore : IDisposable
{
    /// <summary>Checks that both the exact text and its corresponding embedding are stored.</summary>
    bool ContainsCurrent(string id, string text);

    void AddOrUpdate(string id, string text, float[] embedding);
}
