namespace TokenSaver;

/// <summary>Preserves a managed CLI's upstream destination when it starts another VibeRails process.</summary>
public static class LlmProxyUpstream
{
    /// <summary>Original Claude destination; contains a validated base URL or an explicit default marker.</summary>
    public const string ClaudeVariable = "VIBERAILS_CLAUDE_UPSTREAM_BASE";
    /// <summary>Distinguishes a known provider-default launch from missing inheritance metadata.</summary>
    public const string ProviderDefault = "provider-default";
    /// <summary>Invalid as a URL so an unrecoverable destination always fails closed at the relay.</summary>
    public const string Unavailable = "viberails-upstream-unavailable";

    /// <summary>Captures only validated destination metadata, never embedded credentials or a parent relay.</summary>
    public static string Capture(string? upstream)
    {
        if (!EnterpriseNetworkProxy.TryNormalize(upstream, out var normalized)) return Unavailable;
        if (normalized.Length == 0) return ProviderDefault;
        return IsLocalRelay(normalized) ? Unavailable : normalized;
    }

    /// <summary>Resolves explicit settings, direct CLI configuration, or the destination behind a parent relay.</summary>
    public static string Resolve(string? saved, string? inherited, string? captured)
    {
        if (!string.IsNullOrWhiteSpace(saved)) return saved;
        if (string.IsNullOrWhiteSpace(inherited)) return "";
        if (!IsLocalRelay(inherited)) return inherited.Trim();
        // No metadata at all: the parent is a VibeRails release from before the capture existed
        // (mixed versions on one machine are supported). Its relay only ever forwarded to the
        // gateway saved in this same settings.json, already checked above, or to the provider
        // default: an inherited corporate base made it leave the CLI unrouted instead, so that base
        // would arrive here directly. The provider default is therefore exactly where the parent's
        // own requests go. Nothing in VibeRails strips this variable from a launch.
        if (captured is null) return "";
        if (captured == ProviderDefault) return "";
        if (string.IsNullOrWhiteSpace(captured) || captured == Unavailable) return Unavailable;
        var normalized = Capture(captured);
        // Empty or recursive metadata must never turn an unknown corporate route into a public route.
        return normalized == ProviderDefault ? Unavailable : normalized;
    }

    private static bool IsLocalRelay(string value) =>
        LlmProxyBaseUrl.TryNormalizeLoopback(value, out var normalized)
        && Uri.TryCreate(normalized, UriKind.Absolute, out var uri)
        && uri.AbsolutePath.StartsWith("/llm/", StringComparison.OrdinalIgnoreCase);
}
