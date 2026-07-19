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
