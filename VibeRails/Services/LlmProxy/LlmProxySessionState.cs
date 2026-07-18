namespace VibeRails.Services.LlmProxy;

/// <summary>
/// Tracks terminal sessions that were launched with a local LLM-proxy endpoint injected. The
/// settings toggle controls new launches; an activation lease keeps an already-launched CLI's
/// authenticated route alive until that terminal exits.
/// </summary>
public interface ILlmProxySessionState
{
    bool OpenCodeProxyActive { get; }

    IDisposable ActivateOpenCodeProxy();
}

public sealed class LlmProxySessionState : ILlmProxySessionState
{
    private int _openCodeProxyActivations;

    public bool OpenCodeProxyActive => Volatile.Read(ref _openCodeProxyActivations) > 0;

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
