using TokenSaver.Minify;

namespace TokenSaver;

public interface ILlmProxySettingsService
{
    /// <summary>
    /// Reads all proxy settings in a single fresh snapshot. Callers that need more than one field
    /// (a session launch reads three; the proxy route reads enabled+mode) must use one snapshot so
    /// they can't observe a torn combination across a concurrent settings save, and so a launch
    /// hits the disk once instead of once per property.
    /// </summary>
    LlmProxySettings GetSettings();
}

/// <summary>
/// Immutable snapshot of the LLM-proxy settings, with the mode already normalized and the
/// token-saver level already resolved into flags/options/allowlist (the host's settings service
/// owns that mapping — see <see cref="Minify.TokenSaverPresets"/>). The token-saver fields default
/// to "enabled but no-op" so callers that only care about relay routing (and the tests that fake
/// them) don't have to spell them out — a default snapshot behaves as a pure passthrough. Null
/// provider allowlists fall back to their rewriter defaults.
/// </summary>
public sealed record LlmProxySettings(
    bool CodexLlmProxyEnabled,
    string CodexLlmProxyMode,
    bool ClaudeLlmProxyEnabled,
    bool ClaudeTokenSaverEnabled = true,
    MinifyFlags ClaudeTokenSaverFlags = default,
    CondenseOptions ClaudeTokenSaverCondense = default,
    IReadOnlyList<string>? ClaudeTokenSaverAllowlist = null,
    bool CodexTokenSaverEnabled = false,
    MinifyFlags CodexTokenSaverFlags = default,
    CondenseOptions CodexTokenSaverCondense = default,
    IReadOnlyList<string>? CodexTokenSaverAllowlist = null);

public static class CodexLlmProxySettings
{
    public const string ModeSubscription = "subscription";
    public const string ModeApi = "api";

    public static string NormalizeMode(string? mode)
    {
        var trimmed = (mode ?? string.Empty).Trim();
        return trimmed.Equals(ModeApi, StringComparison.OrdinalIgnoreCase)
            ? ModeApi
            : ModeSubscription;
    }
}
