namespace VibeRails.Services.BertV2;

/// <summary>Loads the native model on demand and releases it after two minutes without inference.</summary>
public sealed class IdleBertV2BgeEmbedder : IBertV2BgeEmbedder
{
    private readonly Lock gate = new();
    private readonly Func<IBertV2BgeEmbedder> create;
    private readonly ITimer timer;
    private readonly TimeProvider clock;
    private readonly TimeSpan idleTimeout;
    private IBertV2BgeEmbedder? model;
    private DateTimeOffset lastUsed;
    private bool disposed;

    /// <summary>Creates a lazy owner; the clock and timeout can be supplied by deterministic tests.</summary>
    public IdleBertV2BgeEmbedder(Func<IBertV2BgeEmbedder> create, TimeProvider? clock = null, TimeSpan? idleTimeout = null)
    {
        this.create = create;
        this.clock = clock ?? TimeProvider.System;
        this.idleTimeout = idleTimeout ?? TimeSpan.FromMinutes(2);
        if (this.idleTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(idleTimeout));
        timer = this.clock.CreateTimer(_ => ReleaseIdle(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Serializes inference with eviction so native resources cannot be disposed during a query.</summary>
    public float[] GenerateEmbedding(string text)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            try
            {
                model ??= create();
                return model.GenerateEmbedding(text);
            }
            finally
            {
                lastUsed = clock.GetUtcNow();
                timer.Change(idleTimeout, Timeout.InfiniteTimeSpan);
            }
        }
    }

    private void ReleaseIdle()
    {
        lock (gate)
        {
            if (disposed || model is null) return;
            // A queued callback may belong to the previous inference's deadline.
            var remaining = idleTimeout - (clock.GetUtcNow() - lastUsed);
            if (remaining > TimeSpan.Zero) { timer.Change(remaining, Timeout.InfiniteTimeSpan); return; }
            model.Dispose();
            model = null;
        }
    }

    /// <summary>Releases both timer and native model on host shutdown.</summary>
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            timer.Dispose();
            model?.Dispose();
            model = null;
        }
    }
}
