namespace TokenSaver;

public static class LlmProxyCodexConfig
{
    public const string OpenAiProxyPath = "/llm/openai";
    public const string OpenAiProviderName = "viberails_openai_proxy";
    public const string SessionHeaderName = "viberails_session";
    public const string TabHeaderName = "viberails_tab";
    private const string OpenAiApiPath = "/v1";
    private const string ChatGptCodexApiPath = "/backend-api/codex";

    public static string BuildOpenAiBaseUrl(string apiBaseUrl) =>
        string.Concat(LlmProxyBaseUrl.Normalize(apiBaseUrl), OpenAiProxyPath, OpenAiApiPath);

    public static string BuildChatGptBaseUrl(string apiBaseUrl) =>
        string.Concat(LlmProxyBaseUrl.Normalize(apiBaseUrl), OpenAiProxyPath, ChatGptCodexApiPath);

    /// <summary>
    /// Builds the Codex <c>--config</c> args that point it at the proxy. The session/tab env-var
    /// names are parameters because that contract is owned by the host app's tool-API environment
    /// (<c>LocalToolApiContext</c>), not by this library — the caller passes the same names it
    /// injects into the session environment, so the two can't drift apart.
    /// </summary>
    public static string[] BuildCodexProxyArgs(
        string apiBaseUrl, string mode, string sessionTokenEnvVar, string tabTokenEnvVar)
    {
        var normalizedMode = CodexLlmProxySettings.NormalizeMode(mode);
        var baseUrl = normalizedMode == CodexLlmProxySettings.ModeApi
            ? BuildOpenAiBaseUrl(apiBaseUrl)
            : BuildChatGptBaseUrl(apiBaseUrl);
        return BuildOpenAiProviderArgs(baseUrl, sessionTokenEnvVar, tabTokenEnvVar);
    }

    private static string[] BuildOpenAiProviderArgs(
        string baseUrl, string sessionTokenEnvVar, string tabTokenEnvVar)
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
            $"model_providers.{OpenAiProviderName}.env_http_headers.{SessionHeaderName}=\"{sessionTokenEnvVar}\"",
            "--config",
            $"model_providers.{OpenAiProviderName}.env_http_headers.{TabHeaderName}=\"{tabTokenEnvVar}\""
        ];
    }
}
