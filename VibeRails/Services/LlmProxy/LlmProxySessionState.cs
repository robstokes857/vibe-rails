namespace VibeRails.Services.LlmProxy;

/// <summary>
/// Which provider's traffic this process's LLM proxy was wired up to relay. A proxy child serves
/// the single CLI its terminal tab launched, so at most one of these is ever meaningful per process.
/// </summary>
public enum LlmProxyProvider
{
    /// <summary>No proxied launch has happened in this process yet.</summary>
    None = 0,
    Claude,
    Codex,
    OpenCode
}

/// <summary>
/// Tracks terminal sessions that were launched with a local LLM-proxy endpoint injected. The
/// settings toggle controls new launches; an activation lease keeps an already-launched CLI's
/// authenticated route alive until that terminal exits.
/// </summary>
public interface ILlmProxySessionState
{
    bool OpenCodeProxyActive { get; }

    IDisposable ActivateOpenCodeProxy();

    /// <summary>
    /// The provider whose traffic this process proxies, or <see cref="LlmProxyProvider.None"/>
    /// before any proxied launch. Read by anything that must answer a question about "the saver"
    /// rather than "a saver" — the toggles are per provider, so without this the only available
    /// answer is a meaningless OR across all three.
    /// </summary>
    LlmProxyProvider ActiveProvider { get; }

    /// <summary>
    /// Records that a CLI was launched with this process's proxy details for
    /// <paramref name="provider"/>. Last write wins: a tab launches one CLI, and re-launching into
    /// the same tab replaces the previous answer rather than accumulating.
    /// </summary>
    void RecordProxiedLaunch(LlmProxyProvider provider);
}

public sealed class LlmProxySessionState : ILlmProxySessionState
{
    private int _openCodeProxyActivations;

    // Written when a terminal is prepared, read from the token-saver control endpoint on another
    // thread. int rather than the enum because that is what Volatile can carry.
    private int _activeProvider = (int)LlmProxyProvider.None;

    public bool OpenCodeProxyActive => Volatile.Read(ref _openCodeProxyActivations) > 0;

    public LlmProxyProvider ActiveProvider => (LlmProxyProvider)Volatile.Read(ref _activeProvider);

    public void RecordProxiedLaunch(LlmProxyProvider provider) =>
        Volatile.Write(ref _activeProvider, (int)provider);

    public IDisposable ActivateOpenCodeProxy()
    {
        Interlocked.Increment(ref _openCodeProxyActivations);
        return new ActivationLease(this);
    }

    private void ReleaseOpenCodeProxy() =>
        Interlocked.Decrement(ref _openCodeProxyActivations);

    private sealed class ActivationLease(LlmProxySessionState owner) : IDisposable
    {
        private LlmProxySessionState? _owner = owner;

        public void Dispose() =>
            Interlocked.Exchange(ref _owner, null)?.ReleaseOpenCodeProxy();
    }
}
