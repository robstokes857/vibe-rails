using VibeRails.Auth;

namespace VibeRails.Services.AgentTools;

public sealed class LocalToolApiContext : ILocalToolApiContext
{
    public const string ApiBaseUrlVariable = "VIBERAILS_TOOL_API_BASE";
    public const string SessionTokenVariable = "VIBERAILS_TOOL_SESSION_TOKEN";
    public const string TabTokenVariable = "VIBERAILS_TOOL_TAB_TOKEN";
    public const string CurrentTabIdVariable = "VIBERAILS_TOOL_CURRENT_TAB_ID";
    public const string CurrentSessionIdVariable = "VIBERAILS_TOOL_CURRENT_SESSION_ID";

    private readonly AgentCredentialService _credentials;
    private sealed record ParentContact(string Session, string Tab);
    private ParentContact? parentContact;
    private readonly string _fallbackApiBaseUrl;

    public LocalToolApiContext(string fallbackApiBaseUrl, IAuthService authService, AgentCredentialService? credentials = null)
    {
        _fallbackApiBaseUrl = NormalizeBaseUrl(fallbackApiBaseUrl);
        _credentials = credentials ?? new AgentCredentialService();
    }

    public string ApiBaseUrl =>
        NormalizeBaseUrl(Environment.GetEnvironmentVariable(ApiBaseUrlVariable) ?? _fallbackApiBaseUrl);

    public string SessionToken =>
        parentContact?.Session ?? Environment.GetEnvironmentVariable(SessionTokenVariable) ?? _credentials.ForTools("native").Session;

    public string TabToken =>
        parentContact?.Tab ?? Environment.GetEnvironmentVariable(TabTokenVariable) ?? _credentials.ForTools("native").Tab;

    public string? CurrentTabId =>
        EmptyToNull(Environment.GetEnvironmentVariable(CurrentTabIdVariable));

    public IReadOnlyDictionary<string, string> BuildEnvironment(string? currentSessionId = null)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ApiBaseUrlVariable] = ApiBaseUrl,
            [SessionTokenVariable] = SessionToken,
            [TabTokenVariable] = TabToken
        };

        if (!string.IsNullOrWhiteSpace(CurrentTabId))
            env[CurrentTabIdVariable] = CurrentTabId!;

        if (!string.IsNullOrWhiteSpace(currentSessionId))
        {
            env[CurrentSessionIdVariable] = currentSessionId!;
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(SessionTokenVariable)))
            {
                var contact = _credentials.ForTools(currentSessionId);
                _credentials.BindSession(currentSessionId, currentSessionId);
                env[SessionTokenVariable] = contact.Session;
                env[TabTokenVariable] = contact.Tab;
            }
        }

        return env;
    }

    public IReadOnlyDictionary<string, string> BuildEnvironmentForTab(string tabId)
    {
        var contact = _credentials.PrepareTools(tabId);
        return new Dictionary<string, string>
        {
            [ApiBaseUrlVariable] = _fallbackApiBaseUrl,
            [SessionTokenVariable] = contact.Session,
            [TabTokenVariable] = contact.Tab,
            [CurrentTabIdVariable] = tabId
        };
    }

    public void RevokeTab(string tabId) => _credentials.Revoke(tabId);
    public void CancelTabLaunch(string tabId) => _credentials.CancelPreparedTools(tabId);
    public void AcceptParentContact(string session, string tab) => parentContact = new(session, tab);
    public void BindSession(string tabId, string sessionId) => _credentials.BindSession(tabId, sessionId);

    /// <summary>
    /// Normalizes a local API base URL. Delegates to the TokenSaver library's
    /// <see cref="TokenSaver.LlmProxyBaseUrl.Normalize"/> — the single source of truth shared with
    /// the LLM-proxy config builders — so every base URL is normalized one way.
    /// </summary>
    public static string NormalizeBaseUrl(string value) =>
        TokenSaver.LlmProxyBaseUrl.Normalize(value);

    private static string? EmptyToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
