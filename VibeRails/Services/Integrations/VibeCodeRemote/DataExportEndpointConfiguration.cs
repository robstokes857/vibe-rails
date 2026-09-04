namespace VibeRails.Services.Integrations.VibeCodeRemote;

/// <summary>
/// Shared configuration contract for session-data uploads and the Settings capability flag.
/// Keeping the parser here ensures both surfaces accept exactly the same endpoint shapes.
/// </summary>
internal static class DataExportEndpointConfiguration
{
    internal const string ExportUrlSettingKey = "VibeRails:ExportUrl";

    /// <summary>
    /// Accepts only absolute HTTPS base URLs without a query or fragment. Session and chunk paths
    /// are appended to this URI, so either suffix would make those requests target the wrong path.
    /// </summary>
    internal static bool TryParseExportUri(string? configured, out Uri exportUri)
    {
        exportUri = null!;

        if (string.IsNullOrWhiteSpace(configured))
            return false;
        if (!Uri.TryCreate(configured.Trim(), UriKind.Absolute, out var parsed))
            return false;
        if (!string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.IsNullOrEmpty(parsed.Query) || !string.IsNullOrEmpty(parsed.Fragment))
            return false;

        exportUri = parsed;
        return true;
    }
}
