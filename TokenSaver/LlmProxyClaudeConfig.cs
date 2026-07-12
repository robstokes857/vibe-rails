namespace TokenSaver;

/// <summary>
/// Anthropic/Claude Code side of the local LLM proxy. Mirrors <see cref="LlmProxyCodexConfig"/>
/// (OpenAI/Codex), but Claude points at the proxy through environment variables, not CLI
/// <c>--config</c> args: <c>ANTHROPIC_BASE_URL</c> routes the Messages API through us and
/// <c>ANTHROPIC_CUSTOM_HEADERS</c> carries the same session/tab auth headers the proxy validates.
///
/// Claude Code appends <c>/v1/messages</c> to the base URL, so the base URL must NOT include
/// <c>/v1</c> (unlike the Codex base URL, which does).
/// </summary>
public static class LlmProxyClaudeConfig
{
    public const string AnthropicProxyPath = "/llm/anthropic";
    public const string UpstreamHost = "api.anthropic.com";

    // Same header names the proxy auth-gate checks. Shared with the Codex path so the proxy
    // validates one contract regardless of which CLI is calling.
    public const string SessionHeaderName = LlmProxyCodexConfig.SessionHeaderName;
    public const string TabHeaderName = LlmProxyCodexConfig.TabHeaderName;

    // Env vars Claude Code honors. Values are literal (no interpolation), so we embed real tokens.
    public const string BaseUrlVariable = "ANTHROPIC_BASE_URL";
    public const string CustomHeadersVariable = "ANTHROPIC_CUSTOM_HEADERS";

    /// <summary>
    /// Builds the <c>ANTHROPIC_BASE_URL</c> value. Claude POSTs to <c>{base}/v1/messages</c>, so we
    /// hand it <c>{apiBaseUrl}/llm/anthropic</c> and our route catches <c>/llm/anthropic/v1/messages</c>.
    /// </summary>
    public static string BuildAnthropicBaseUrl(string apiBaseUrl) =>
        string.Concat(LlmProxyBaseUrl.Normalize(apiBaseUrl), AnthropicProxyPath);

    /// <summary>
    /// Builds the <c>ANTHROPIC_CUSTOM_HEADERS</c> value: newline-separated <c>Name: Value</c> pairs
    /// carrying the session/tab tokens the proxy validates. These ride an env var (not a CLI arg),
    /// so the token values never surface in the process command line.
    /// </summary>
    public static string BuildCustomHeaders(string sessionToken, string tabToken)
    {
        return string.Concat(
            SessionHeaderName, ": ", sessionToken, "\n",
            TabHeaderName, ": ", tabToken);
    }

    /// <summary>
    /// The env vars that point Claude Code at the proxy. Merged into the prepared-session
    /// environment at the <c>CommandService</c> seam.
    /// </summary>
    public static IReadOnlyDictionary<string, string> BuildClaudeProxyEnvironment(
        string apiBaseUrl, string sessionToken, string tabToken)
    {
        return new Dictionary<string, string>
        {
            [BaseUrlVariable] = BuildAnthropicBaseUrl(apiBaseUrl),
            [CustomHeadersVariable] = BuildCustomHeaders(sessionToken, tabToken)
        };
    }
}
