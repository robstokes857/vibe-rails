using VibeRails.Auth;

namespace VibeRails.Services.AgentTools;

public sealed class LocalToolApiContext : ILocalToolApiContext
{
    public const string ApiBaseUrlVariable = "VIBERAILS_TOOL_API_BASE";
    public const string SessionTokenVariable = "VIBERAILS_TOOL_SESSION_TOKEN";
    public const string TabTokenVariable = "VIBERAILS_TOOL_TAB_TOKEN";
    public const string CurrentTabIdVariable = "VIBERAILS_TOOL_CURRENT_TAB_ID";
    public const string CurrentSessionIdVariable = "VIBERAILS_TOOL_CURRENT_SESSION_ID";

    private readonly IAuthService _authService;
    private readonly string _fallbackApiBaseUrl;

    public LocalToolApiContext(string fallbackApiBaseUrl, IAuthService authService)
    {
        _fallbackApiBaseUrl = NormalizeBaseUrl(fallbackApiBaseUrl);
        _authService = authService;
    }

    public string ApiBaseUrl =>
        NormalizeBaseUrl(Environment.GetEnvironmentVariable(ApiBaseUrlVariable) ?? _fallbackApiBaseUrl);

    public string SessionToken =>
        Environment.GetEnvironmentVariable(SessionTokenVariable) ?? _authService.GetInstanceToken();

    public string TabToken =>
        Environment.GetEnvironmentVariable(TabTokenVariable) ?? _authService.GetTabToken();

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
            env[CurrentSessionIdVariable] = currentSessionId!;

        return env;
    }

    /// <summary>
    /// Normalizes a local API base URL: blank/whitespace falls back to the sentinel
    /// <c>http://127.0.0.1:0</c>, then it is trimmed and stripped of trailing slashes. Shared with
    /// the LLM-proxy config builders so every base URL is normalized one way.
    /// </summary>
    public static string NormalizeBaseUrl(string value)
    {
        var trimmed = string.IsNullOrWhiteSpace(value)
            ? "http://127.0.0.1:0"
            : value.Trim();

        return trimmed.TrimEnd('/');
    }

    private static string? EmptyToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
