using TokenSaver;
using VibeRails.Auth;

namespace VibeRails.Services.LlmProxy;

/// <summary>
/// Connection details for the LLM proxy hosted by the current VibeRails process.
/// This is deliberately separate from <c>ILocalToolApiContext</c>: terminal-tab children inherit
/// the root tool API context, while their Claude/Codex traffic must stay in the child — the child
/// owns that traffic's relay lifetime, auth tokens, and savings attribution.
/// </summary>
public interface ILocalLlmProxyContext
{
    string ApiBaseUrl { get; }
    string SessionToken { get; }
    string TabToken { get; }
}

public sealed class LocalLlmProxyContext : ILocalLlmProxyContext
{
    public const string SessionTokenVariable = "VIBERAILS_LLM_PROXY_SESSION_TOKEN";
    public const string TabTokenVariable = "VIBERAILS_LLM_PROXY_TAB_TOKEN";

    /// <summary>
    /// The proxy host a CLI-spawned helper should talk to, e.g. an MCP server started by
    /// <c>claude mcp add ... -- vb mcp</c>. Every provider already reaches the proxy, but by a
    /// different route: Claude reads <c>ANTHROPIC_BASE_URL</c>, Codex gets its base URL in a
    /// <c>--config</c> arg, and OpenCode gets it inside <c>OPENCODE_CONFIG_CONTENT</c> JSON. None of
    /// those is a contract a generic child can read, so this variable states it once, the same way,
    /// for all of them. Paired with <see cref="SessionTokenVariable"/> and
    /// <see cref="TabTokenVariable"/> — the proxy requires both tokens.
    /// </summary>
    public const string BaseUrlVariable = "VIBERAILS_LLM_PROXY_BASE";

    private readonly IAuthService _authService;

    public LocalLlmProxyContext(string apiBaseUrl, IAuthService authService)
    {
        ApiBaseUrl = LlmProxyBaseUrl.Normalize(apiBaseUrl);
        _authService = authService;
    }

    public string ApiBaseUrl { get; }
    public string SessionToken => _authService.GetInstanceToken();
    public string TabToken => _authService.GetTabToken();
}
