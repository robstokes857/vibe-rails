namespace VibeRails.Services.BertV2;

/// <summary>
/// Owns a native model that loads on first use and is released after a period without use, so a
/// root or <c>vb mcp</c> process does not hold it for its whole life (VIBE-254 F3). Use and release
/// share a lock, so the model is never disposed during a call.
/// </summary>
public sealed class IdleModelOwner<T> : IDisposable where T : class, IDisposable
{
    private readonly Lock gate = new();
    private readonly Func<T> create;
    private readonly ITimer timer;
    private readonly TimeProvider clock;
    private readonly TimeSpan idleTimeout;
    private T? model;
    private DateTimeOffset lastUsed;
    private bool disposed;

    /// <summary>Creates a lazy owner; the clock and timeout can be supplied by deterministic tests.</summary>
    public IdleModelOwner(Func<T> create, TimeProvider? clock = null, TimeSpan? idleTimeout = null)
    {
        this.create = create;
        this.clock = clock ?? TimeProvider.System;
        this.idleTimeout = idleTimeout ?? TimeSpan.FromMinutes(2);
        if (this.idleTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(idleTimeout));
        timer = this.clock.CreateTimer(_ => ReleaseIdle(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Runs <paramref name="work"/> with the loaded model, loading it first if needed.</summary>
    public TResult Use<TResult>(Func<T, TResult> work)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            try
            {
                model ??= create();
                return work(model);
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
            // A queued callback may belong to the previous call's deadline.
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
