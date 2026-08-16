namespace TokenSaver;

/// <summary>
/// OpenCode / xAI (Grok) side of the local LLM proxy. Mirrors <see cref="LlmProxyZaiConfig"/>
/// (Z.AI/GLM): OpenCode is pointed at the proxy through the same
/// <c>OPENCODE_CONFIG_CONTENT</c> env var, which overrides the <c>xai</c> provider's
/// <c>options.baseURL</c> and <c>options.headers</c>. Credentials stay in the user's global
/// <c>auth.json</c> — <c>XDG_DATA_HOME</c> is left unchanged.
///
/// Upstream: xAI's OpenAI-compatible Chat Completions API lives at <c>https://api.x.ai/v1</c>.
/// The proxy strips <c>/llm/xai/</c> and forwards the remainder, so a request to
/// <c>/llm/xai/v1/chat/completions</c> reaches <c>https://api.x.ai/v1/chat/completions</c>.
///
/// This is a Kestrel-mapped route on the main host — not a second listener. The rejected
/// 2026-08-15 <c>GrokLoopbackBridge</c> / <c>/llm/grok</c> sidecar must not return
/// (see <c>API_SEC.md</c>).
/// </summary>
public static class LlmProxyXaiConfig
{
    public const string XaiProxyPath = "/llm/xai";

    /// <summary>xAI's public OpenAI-compatible API host. Chat Completions lives under
    /// <c>/v1</c>.</summary>
    public const string UpstreamHost = "api.x.ai";

    public const string UpstreamApiPath = "/v1";

    public const string SessionHeaderName = LlmProxyCodexConfig.SessionHeaderName;
    public const string TabHeaderName = LlmProxyCodexConfig.TabHeaderName;

    /// <summary>
    /// Builds the <c>baseURL</c> value handed to OpenCode's <c>xai</c> provider. OpenCode appends
    /// <c>/chat/completions</c> to this, so the request path becomes
    /// <c>/llm/xai/v1/chat/completions</c> and the route catches it under
    /// <see cref="XaiProxyPath"/>.
    /// </summary>
    public static string BuildXaiBaseUrl(string apiBaseUrl) =>
        string.Concat(LlmProxyBaseUrl.Normalize(apiBaseUrl), XaiProxyPath, UpstreamApiPath);
}
