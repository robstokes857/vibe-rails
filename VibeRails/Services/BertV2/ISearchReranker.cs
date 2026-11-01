namespace VibeRails.Services.BertV2;

/// <summary>
/// Rescores retrieved passages against a query (VIBE-257). A cross-encoder reads the query and
/// each passage together, so it judges relevance more precisely than comparing two independent
/// embeddings, but it is too slow to run over a whole corpus. It reorders the top hybrid candidates.
/// </summary>
public interface ISearchReranker : IDisposable
{
    /// <summary>False while the model is not installed; search then keeps today's hybrid order and pool sizes.</summary>
    bool IsAvailable { get; }

    /// <summary>Returns one relevance score per passage, in passage order. Higher is more relevant.</summary>
    float[] Score(string query, IReadOnlyList<string> passages);
}
