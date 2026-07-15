using TokenSaver;
using TokenSaver.Pipeline;
using VibeRails.Utils;

namespace VibeRails.Services.LlmProxy;

/// <summary>
/// Host-side implementation of the TokenSaver library's settings seam: reads the proxy settings
/// from settings.json (via <see cref="Config"/>) in one fresh snapshot. The interface and the
/// snapshot record live in the library; only this disk-coupled reader stays in the app.
/// </summary>
public sealed class LlmProxySettingsService : ILlmProxySettingsService
{
    public LlmProxySettings GetSettings()
    {
        return Resolve(Config.LoadFresh());
    }

    internal static LlmProxySettings Resolve(Settings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        // TokenSaverStages is the whole decision. Null means never-configured and resolves to the
        // catalog defaults; an empty list is a real "everything off" choice and is honored — hence
        // the nullable round-trip rather than a `?? []`.
        //
        // Config.LoadCore migrates legacy tier/per-transform settings before they reach this seam,
        // so runtime resolution has exactly one source of truth.
        var plan = CompressionCatalog.Resolve(settings.TokenSaverStages);

        // The saver's master kill switch. Named for Claude for legacy reasons; it governs both.
        var tokenSaverEnabled = settings.ClaudeTokenSaverEnabled;

        return new LlmProxySettings(
            CodexLlmProxyEnabled: settings.CodexLlmProxyEnabled,
            CodexLlmProxyMode: CodexLlmProxySettings.NormalizeMode(settings.CodexLlmProxyMode),
            ClaudeLlmProxyEnabled: settings.ClaudeLlmProxyEnabled,
            ClaudeTokenSaverEnabled: settings.ClaudeLlmProxyEnabled && tokenSaverEnabled,
            CodexTokenSaverEnabled: settings.CodexLlmProxyEnabled && tokenSaverEnabled,
            TokenSaverPlan: plan,
            TokenSaverCaptureEnabled: settings.TokenSaverCaptureEnabled);
    }
}
