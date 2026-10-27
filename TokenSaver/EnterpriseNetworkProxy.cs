namespace TokenSaver;

/// <summary>Validates and applies optional enterprise API gateway base URLs.</summary>
public static class EnterpriseNetworkProxy
{
    public const string InvalidMessage =
        "Enterprise Network Proxy must be an HTTP or HTTPS API base URL without embedded credentials, a query, or a fragment.";

    /// <summary>Returns a normalized API base URL, or an empty string for the provider default.</summary>
    public static string Normalize(string? value) =>
        TryNormalize(value, out var normalized) ? normalized : throw new ArgumentException(InvalidMessage);

    /// <summary>
    /// <see cref="Normalize"/> without the exception: false for a value that is not an API base.
    /// Blank is valid and normalizes to an empty string (the provider default).
    /// </summary>
    public static bool TryNormalize(string? value, out string normalized)
    {
        normalized = string.Empty;
        var trimmed = value?.Trim() ?? string.Empty;
        if (trimmed.Length == 0) return true;
        if (trimmed.Length > 2048 || trimmed.Any(char.IsControl)
            || !Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrEmpty(uri.Host) || uri.Port == 0
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
        {
            return false;
        }
        normalized = uri.AbsoluteUri.TrimEnd('/');
        return true;
    }

    /// <summary>
    /// Replaces the provider's API base, retaining the endpoint suffix and request query.
    /// The gateway supplies its complete API base (for example https://gateway.example/v1).
    /// Building the path separately keeps request-controlled paths from replacing its origin.
    /// </summary>
    public static Uri BuildTarget(Uri providerTarget, string? gatewayBaseUrl, string providerApiPath) =>
        TryBuildTarget(providerTarget, gatewayBaseUrl, providerApiPath, out var target)
            ? target
            : throw new ArgumentException(InvalidMessage);

    /// <summary>
    /// <see cref="BuildTarget"/> for a stored value: false when the saved gateway does not
    /// validate (settings saves validate, so a hand-edited settings.json). Callers fail closed
    /// and never fall back to the provider default, a destination the organization may forbid.
    /// With <paramref name="addMissingApiPath"/>, a base given without the API path gets it (see
    /// <see cref="WithApiPath"/>).
    /// </summary>
    public static bool TryBuildTarget(Uri providerTarget, string? gatewayBaseUrl, string providerApiPath, out Uri target,
        bool addMissingApiPath = false)
    {
        target = providerTarget;
        if (!TryNormalize(gatewayBaseUrl, out var normalized)) return false;
        if (normalized.Length == 0) return true;
        if (addMissingApiPath) normalized = WithApiPath(normalized, providerApiPath);
        var suffix = providerTarget.AbsolutePath;
        if (suffix.Equals(providerApiPath, StringComparison.OrdinalIgnoreCase)
            || suffix.StartsWith(providerApiPath + "/", StringComparison.OrdinalIgnoreCase))
            suffix = suffix[providerApiPath.Length..];
        var builder = new UriBuilder(normalized);
        builder.Path = builder.Path.TrimEnd('/') + "/" + suffix.TrimStart('/');
        builder.Query = providerTarget.Query;
        target = builder.Uri;
        return true;
    }

    /// <summary>
    /// Appends <paramref name="apiPath"/> unless the normalized base already ends with it. Claude
    /// Code adds /v1 to ANTHROPIC_BASE_URL itself, so Claude gateways are handed out both ways:
    /// https://gw.example (what that variable takes) and https://gw.example/v1. Both must work.
    /// </summary>
    public static string WithApiPath(string normalizedBase, string apiPath) =>
        normalizedBase.EndsWith(apiPath, StringComparison.OrdinalIgnoreCase)
            ? normalizedBase
            : normalizedBase + apiPath;
}
