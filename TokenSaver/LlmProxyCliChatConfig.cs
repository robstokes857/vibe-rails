namespace TokenSaver;

/// <summary>
/// Native Grok Build CLI chat-proxy side of the local LLM proxy. Grok's default
/// inference host is <c>cli-chat-proxy.grok.com</c> (session tokens from
/// <c>grok login</c>). This is a Kestrel-mapped route on the main host — not
/// <c>/llm/grok</c> and not a second listener (see <c>API_SEC.md</c>).
///
/// Do not point native Grok at <see cref="LlmProxyXaiConfig"/> / <c>api.x.ai</c>:
/// setting <c>GROK_MODELS_BASE_URL</c> switches Grok to API-key auth and
/// <c>grok login</c> tokens are minted for the cli-chat-proxy, not <c>api.x.ai</c>.
/// </summary>
public static class LlmProxyCliChatConfig
{
    public const string CliChatProxyPath = "/llm/cli-chat";

    public const string SubscriptionUpstreamHost = "cli-chat-proxy.grok.com";

    public const string ApiUpstreamHost = LlmProxyXaiConfig.UpstreamHost;

    public const string UpstreamApiPath = "/v1";

    public static string NormalizeMode(string? mode) => CodexLlmProxySettings.NormalizeMode(mode);

    public static string ResolveUpstreamHost(string? mode) =>
        NormalizeMode(mode) == CodexLlmProxySettings.ModeApi
            ? ApiUpstreamHost
            : SubscriptionUpstreamHost;

    public const string SessionHeaderName = LlmProxyCodexConfig.SessionHeaderName;
    public const string TabHeaderName = LlmProxyCodexConfig.TabHeaderName;

    /// <summary>
    /// Value for <c>GROK_CLI_CHAT_PROXY_BASE_URL</c>. Grok appends its model's backend path —
    /// <c>/responses</c> for grok-4.6 (Responses backend), <c>/chat/completions</c> for
    /// grok-build and API-mode custom-model calls — plus ancillary <c>/models</c>,
    /// <c>/settings</c>, <c>/bundle/archive</c>, <c>/feedback/config</c> and <c>/user</c> GETs
    /// (all observed against grok 1.0.5). The catch-all route relays every one of them; the
    /// ancillary GETs carry no <c>viberails_*</c> headers (header injection is per-model and
    /// inference-only), get 401ed by the middleware, and grok tolerates that and still runs
    /// the authenticated inference POST.
    /// </summary>
    public static string BuildCliChatBaseUrl(string apiBaseUrl) =>
        string.Concat(LlmProxyBaseUrl.Normalize(apiBaseUrl), CliChatProxyPath, UpstreamApiPath);
}
