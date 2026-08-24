using TokenSaver.Pipeline;

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
/// Immutable snapshot of the LLM-proxy settings, with the mode already normalized and the enabled
/// stage ids already resolved into flags/options/allowlist (the host's settings service owns that
/// mapping — it resolves them through <see cref="Pipeline.CompressionCatalog.Resolve"/>). The
/// token-saver fields default to "enabled but no-op" so callers that only care about relay routing
/// (and the tests that fake them) don't have to spell them out — a default snapshot behaves as a
/// pure passthrough.
/// </summary>
public sealed record LlmProxySettings(
    bool CodexLlmProxyEnabled,
    string CodexLlmProxyMode,
    bool ClaudeLlmProxyEnabled,
    bool OpenCodeLlmProxyEnabled = false,
    bool ClaudeTokenSaverEnabled = true,
    bool CodexTokenSaverEnabled = false,
    bool OpenCodeTokenSaverEnabled = false,
    CompressionPlan? TokenSaverPlan = null,
    bool TokenSaverCaptureEnabled = false,
    bool GrokLlmProxyEnabled = false,
    string GrokLlmProxyMode = CodexLlmProxySettings.ModeSubscription,
    bool GrokTokenSaverEnabled = false)
{
    /// <summary>
    /// The persisted setting that controls whether a newly launched OpenCode process receives the
    /// local proxy configuration. <see cref="OpenCodeLlmProxyEnabled"/> may remain true for an
    /// already-active session after this value turns false, so launch code must use this property.
    /// Defaults to the effective value for compatibility with test/host callers that do not need
    /// session-stable routing.
    /// </summary>
    public bool OpenCodeLlmProxyLaunchEnabled { get; init; } = OpenCodeLlmProxyEnabled;

    /// <summary>
    /// Same launch-vs-lease split as <see cref="OpenCodeLlmProxyLaunchEnabled"/> for native Grok.
    /// </summary>
    public bool GrokLlmProxyLaunchEnabled { get; init; } = GrokLlmProxyEnabled;

    /// <summary>
    /// The resolved stage selection (see <see cref="Pipeline.CompressionCatalog"/>), or a no-op
    /// plan when this snapshot predates the stage rework.
    ///
    /// The plan is the only runtime representation of the compression decision. Provider routes
    /// select their allowlist from it; no parallel flag/allowlist projection exists to drift.
    /// </summary>
    public CompressionPlan ResolvedPlan => TokenSaverPlan ?? NoOpPlan;

    private static readonly CompressionPlan NoOpPlan = CompressionPlan.FromLegacy(default, default);
}

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
