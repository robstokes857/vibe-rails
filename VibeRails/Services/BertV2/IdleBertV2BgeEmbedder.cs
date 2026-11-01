namespace VibeRails.Services.BertV2;

/// <summary>Loads the native model on demand and releases it after two minutes without inference.</summary>
public sealed class IdleBertV2BgeEmbedder : IBertV2BgeEmbedder
{
    private readonly IdleModelOwner<IBertV2BgeEmbedder> owner;

    /// <summary>Creates a lazy owner; the clock and timeout can be supplied by deterministic tests.</summary>
    public IdleBertV2BgeEmbedder(Func<IBertV2BgeEmbedder> create, TimeProvider? clock = null, TimeSpan? idleTimeout = null)
        => owner = new(create, clock, idleTimeout);

    /// <summary>Serializes inference with eviction so native resources cannot be disposed during a query.</summary>
    public float[] GenerateEmbedding(string text) => owner.Use(model => model.GenerateEmbedding(text));

    /// <summary>Releases both timer and native model on host shutdown.</summary>
    public void Dispose() => owner.Dispose();
}
