namespace TokenSaver;

public static class LlmProxyCodexConfig
{
    public const string OpenAiProxyPath = "/llm/openai";
    public const string OpenAiProviderName = "viberails_openai_proxy";
    public const string SessionHeaderName = "viberails_session";
    public const string TabHeaderName = "viberails_tab";

    /// <summary>
    /// Correlation header carrying the terminal session id (<c>Sessions.Id</c>) that produced the
    /// request. Unlike the two auth tokens this is metadata, not a credential: the auth gate does
    /// not validate it, and the relay strips it before forwarding upstream alongside the others.
    /// Absent on older launches — exchanges from those record a NULL session id.
    /// </summary>
    public const string TerminalSessionHeaderName = "viberails_terminal_session";

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
    /// <paramref name="terminalSessionEnvVar"/> is the env var Codex resolves at request time for
    /// the terminal-session correlation header; when null (no session) the mapping is omitted
    /// entirely rather than pointing at a variable that does not exist.
    /// </summary>
    public static string[] BuildCodexProxyArgs(
        string apiBaseUrl,
        string mode,
        string sessionTokenEnvVar,
        string tabTokenEnvVar,
        string? terminalSessionEnvVar = null)
    {
        var normalizedMode = CodexLlmProxySettings.NormalizeMode(mode);
        var baseUrl = normalizedMode == CodexLlmProxySettings.ModeApi
            ? BuildOpenAiBaseUrl(apiBaseUrl)
            : BuildChatGptBaseUrl(apiBaseUrl);
        return BuildOpenAiProviderArgs(baseUrl, sessionTokenEnvVar, tabTokenEnvVar, terminalSessionEnvVar);
    }

    private static string[] BuildOpenAiProviderArgs(
        string baseUrl,
        string sessionTokenEnvVar,
        string tabTokenEnvVar,
        string? terminalSessionEnvVar)
    {
        var args = new List<string>
        {
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
        };

        if (!string.IsNullOrWhiteSpace(terminalSessionEnvVar))
        {
            args.Add("--config");
            args.Add(
                $"model_providers.{OpenAiProviderName}.env_http_headers.{TerminalSessionHeaderName}"
                + $"=\"{terminalSessionEnvVar.Trim()}\"");
        }

        return [.. args];
    }
}
