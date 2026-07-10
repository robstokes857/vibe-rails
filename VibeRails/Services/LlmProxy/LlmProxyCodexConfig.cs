using VibeRails.Services.AgentTools;

namespace VibeRails.Services.LlmProxy;

public static class LlmProxyCodexConfig
{
    public const string OpenAiProxyPath = "/llm/openai";
    public const string OpenAiProviderName = "viberails_openai_proxy";
    public const string SessionHeaderName = "viberails_session";
    public const string TabHeaderName = "viberails_tab";
    private const string OpenAiApiPath = "/v1";
    private const string ChatGptCodexApiPath = "/backend-api/codex";

    public static string BuildOpenAiBaseUrl(string apiBaseUrl) =>
        string.Concat(LocalToolApiContext.NormalizeBaseUrl(apiBaseUrl), OpenAiProxyPath, OpenAiApiPath);

    public static string BuildChatGptBaseUrl(string apiBaseUrl) =>
        string.Concat(LocalToolApiContext.NormalizeBaseUrl(apiBaseUrl), OpenAiProxyPath, ChatGptCodexApiPath);

    /// <summary>
    /// True when <paramref name="path"/> targets the OpenAI proxy prefix or one of its children.
    /// Shared with <c>CookieAuthMiddleware</c> so the middleware and route cannot drift apart.
    /// </summary>
    public static bool IsOpenAiProxyPath(string path) =>
        IsPathOrChild(path, OpenAiProxyPath);

    private static bool IsPathOrChild(string path, string prefix) =>
        path.Equals(prefix, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase);

    public static string[] BuildCodexProxyArgs(string apiBaseUrl, string mode)
    {
        var normalizedMode = CodexLlmProxySettings.NormalizeMode(mode);
        var baseUrl = normalizedMode == CodexLlmProxySettings.ModeApi
            ? BuildOpenAiBaseUrl(apiBaseUrl)
            : BuildChatGptBaseUrl(apiBaseUrl);
        return BuildOpenAiProviderArgs(baseUrl);
    }

    private static string[] BuildOpenAiProviderArgs(string baseUrl)
    {
        return
        [
            "--config",
            $"model_provider=\"{OpenAiProviderName}\"",
            "--config",
            $"model_providers.{OpenAiProviderName}.name=\"VibeRails OpenAI Proxy\"",
            "--config",
            $"model_providers.{OpenAiProviderName}.base_url=\"{baseUrl}\"",
            "--config",
            $"model_providers.{OpenAiProviderName}.wire_api=\"responses\"",
            "--config",
            $"model_providers.{OpenAiProviderName}.requires_openai_auth=true",
            "--config",
            $"model_providers.{OpenAiProviderName}.env_http_headers.{SessionHeaderName}=\"{LocalToolApiContext.SessionTokenVariable}\"",
            "--config",
            $"model_providers.{OpenAiProviderName}.env_http_headers.{TabHeaderName}=\"{LocalToolApiContext.TabTokenVariable}\""
        ];
    }
}
