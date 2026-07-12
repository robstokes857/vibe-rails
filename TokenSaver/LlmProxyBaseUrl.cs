namespace TokenSaver;

public static class LlmProxyBaseUrl
{
    /// <summary>
    /// Normalizes a local API base URL: blank/whitespace falls back to the sentinel
    /// <c>http://127.0.0.1:0</c>, then it is trimmed and stripped of trailing slashes. The host
    /// app's <c>LocalToolApiContext.NormalizeBaseUrl</c> delegates here so every base URL — tool
    /// API and LLM proxy alike — is normalized one way.
    /// </summary>
    public static string Normalize(string value)
    {
        var trimmed = string.IsNullOrWhiteSpace(value)
            ? "http://127.0.0.1:0"
            : value.Trim();

        return trimmed.TrimEnd('/');
    }
}
