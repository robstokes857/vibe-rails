using Microsoft.AspNetCore.Http;

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

public static class LlmProxyAuthGateExtensions
{
    /// <summary>
    /// The proxy's auth contract, stated once: the session token <b>and</b> the tab token, both
    /// read from the custom headers the CLI was launched with. Session alone is deliberately not
    /// enough — these endpoints act on behalf of a specific terminal tab.
    ///
    /// Public because the relay is not the only endpoint on this contract: the host's token-saver
    /// control route sits beside it under <c>/llm/</c> and must gate identically rather than
    /// restate the check and drift from it.
    /// </summary>
    public static bool IsRequestAuthenticated(this ILlmProxyAuthGate authGate, HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(authGate);
        ArgumentNullException.ThrowIfNull(context);

        var sessionToken = context.Request.Headers[LlmProxyCodexConfig.SessionHeaderName].FirstOrDefault();
        var tabToken = context.Request.Headers[LlmProxyCodexConfig.TabHeaderName].FirstOrDefault();
        return authGate.ValidateSessionToken(sessionToken) && authGate.ValidateTabToken(tabToken);
    }
}
