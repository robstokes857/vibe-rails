namespace TokenSaver;

/// <summary>
/// Validates the session/tab auth tokens that ride the proxy's custom headers. The host app adapts
/// its own auth service behind this gate so the library never depends on host auth internals.
/// </summary>
public interface ILlmProxyAuthGate
{
    bool ValidateSessionToken(string? token);

    bool ValidateTabToken(string? token);
}
