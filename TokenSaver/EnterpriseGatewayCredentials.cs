namespace TokenSaver;

/// <summary>Validation shared by gateway settings and the outbound relay.</summary>
public static class EnterpriseGatewayCredentials
{
    /// <summary>Validates the supported outgoing authentication formats.</summary>
    public static string NormalizeMode(string? mode) => (mode ?? "bearer").Trim().ToLowerInvariant() switch
    {
        "" or "bearer" => "bearer",
        "api-key" => "api-key",
        _ => throw new ArgumentException("Choose Bearer token or API key header for gateway authentication.")
    };

    /// <summary>Allows HTTP field names without permitting routing or local authentication overrides.</summary>
    public static string NormalizeApiKeyHeader(string? value)
    {
        var name = string.IsNullOrWhiteSpace(value) ? "api-key" : value.Trim();
        if (name.Length > 128 || name.Any(c => !char.IsAsciiLetterOrDigit(c) && !"!#$%&'*+-.^_`|~".Contains(c))
            || name.StartsWith("viberails", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Content-", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase)
            || new[] { "Host", "Cookie", "Set-Cookie", "Connection", "Keep-Alive", "TE", "Trailer", "Transfer-Encoding", "Upgrade", "Accept-Encoding", "Allow", "Expires", "Last-Modified" }
                .Contains(name, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("Enter an API key header such as api-key or x-api-key; routing and local authentication headers are reserved.");
        return name;
    }

    /// <summary>Normalizes a secret according to its selected header format.</summary>
    public static string NormalizeSecret(string? value, string? mode) => NormalizeMode(mode) == "bearer"
        ? NormalizeBearerToken(value) : NormalizeApiKey(value);

    private static string NormalizeApiKey(string? value)
    {
        var key = (value ?? "").Trim();
        if (key.Length > 16384 || key.Any(c => c <= ' ' || c > '~'))
            throw new ArgumentException("Gateway API keys must contain only printable ASCII characters without spaces.");
        return key;
    }

    /// <summary>Applies the gateway credential after validation, replacing conflicting provider credentials.</summary>
    public static void Apply(System.Net.Http.HttpRequestMessage request, string secret, string mode, string apiKeyHeader)
    {
        var normalizedMode = NormalizeMode(mode);
        var header = normalizedMode == "bearer" ? "Authorization" : NormalizeApiKeyHeader(apiKeyHeader);
        var normalizedSecret = NormalizeSecret(secret, normalizedMode);
        foreach (var name in new[] { "Authorization", "x-api-key", "api-key", "chatgpt-account-id", "OpenAI-Organization", "OpenAI-Project", header })
            request.Headers.Remove(name);
        if (!request.Headers.TryAddWithoutValidation(header, normalizedMode == "bearer" ? "Bearer " + normalizedSecret : normalizedSecret))
            throw new ArgumentException("The configured gateway header cannot be used as a request header.");
    }

    /// <summary>Accepts a token alone or a pasted Authorization Bearer value.</summary>
    public static string NormalizeBearerToken(string? value)
    {
        var token = (value ?? string.Empty).Trim();
        if (token.Equals("Bearer", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Enter a token after Bearer.");
        if (token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            token = token[7..].Trim();
            if (token.Length == 0) throw new ArgumentException("Enter a token after Bearer.");
        }
        if (token.Length > 16384 || token.Any(c => c <= ' ' || c > '~'))
            throw new ArgumentException("Gateway bearer tokens must contain only printable ASCII characters without spaces.");
        return token;
    }
}
