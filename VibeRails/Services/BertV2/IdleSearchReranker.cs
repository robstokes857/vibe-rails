namespace VibeRails.Services.BertV2;

/// <summary>Loads the re-ranking model on demand and releases it after two minutes without use (VIBE-257).</summary>
public sealed class IdleSearchReranker : ISearchReranker
{
    private readonly IdleModelOwner<ISearchReranker> owner;
    private readonly Func<bool> available;

    /// <summary>Creates a lazy owner; the clock and timeout can be supplied by deterministic tests.</summary>
    /// <param name="available">A cheap installed check; it never loads the model.</param>
    public IdleSearchReranker(Func<ISearchReranker> create, Func<bool>? available = null, TimeProvider? clock = null,
        TimeSpan? idleTimeout = null)
    {
        owner = new(create, clock, idleTimeout);
        this.available = available ?? (() => true);
    }

    public bool IsAvailable => available();

    public float[] Score(string query, IReadOnlyList<string> passages) => owner.Use(model => model.Score(query, passages));

    public void Dispose() => owner.Dispose();
}
