using VibeRails.Services.AgentTools;

namespace VibeRails.Services.LlmProxy;

public static class LlmProxyCodexConfig
{
    public const string OpenAiProxyPath = "/llm/openai";
    public const string OpenAiProviderName = "viberails_openai_proxy";
    public const string SessionHeaderName = "viberails_session";
    public const string TabHeaderName = "viberails_tab";

    public static string BuildOpenAiBaseUrl(string apiBaseUrl)
    {
        var normalized = string.IsNullOrWhiteSpace(apiBaseUrl)
            ? "http://127.0.0.1:0"
            : apiBaseUrl.Trim();

        while (normalized.EndsWith('/'))
            normalized = normalized[..^1];

        return string.Concat(normalized, OpenAiProxyPath, "/v1");
    }

    public static string BuildChatGptBaseUrl(string apiBaseUrl)
    {
        var normalized = string.IsNullOrWhiteSpace(apiBaseUrl)
            ? "http://127.0.0.1:0"
            : apiBaseUrl.Trim();

        while (normalized.EndsWith('/'))
            normalized = normalized[..^1];

        return string.Concat(normalized, OpenAiProxyPath, "/");
    }

    public static string[] BuildCodexProxyArgs(string apiBaseUrl, string mode)
    {
        var normalizedMode = CodexLlmProxySettings.NormalizeMode(mode);
        return normalizedMode == CodexLlmProxySettings.ModeApi
            ? BuildOpenAiProviderArgs(apiBaseUrl)
            : BuildChatGptProxyArgs(apiBaseUrl);
    }

    private static string[] BuildChatGptProxyArgs(string apiBaseUrl)
    {
        var baseUrl = BuildChatGptBaseUrl(apiBaseUrl);

        return
        [
            "--config",
            $"chatgpt_base_url=\"{baseUrl}\""
        ];
    }

    private static string[] BuildOpenAiProviderArgs(string apiBaseUrl)
    {
        var baseUrl = BuildOpenAiBaseUrl(apiBaseUrl);

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
