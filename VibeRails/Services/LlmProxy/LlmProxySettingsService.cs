using TokenSaver;
using TokenSaver.Pipeline;
using VibeRails.Utils;

namespace VibeRails.Services.LlmProxy;

/// <summary>
/// Host-side implementation of the TokenSaver library's settings seam: reads the proxy settings
/// from settings.json (via <see cref="Config"/>) in one fresh snapshot. The interface and the
/// snapshot record live in the library; only this disk-coupled reader stays in the app.
/// </summary>
public sealed class LlmProxySettingsService(
    ITabTokenSaverState tabTokenSaverState,
    ILlmProxySessionState proxySessionState) : ILlmProxySettingsService
{
    public LlmProxySettings GetSettings()
    {
        return Resolve(
            Config.LoadFresh(),
            tabTokenSaverState.Enabled,
            proxySessionState.OpenCodeProxyActive);
    }

    internal static LlmProxySettings Resolve(
        Settings settings,
        bool tabTokenSaverEnabled,
        bool openCodeProxyActive = false)
    {
        ArgumentNullException.ThrowIfNull(settings);

        // TokenSaverStages is the whole decision. Null means never-configured and resolves to the
        // catalog defaults; an empty list is a real "everything off" choice and is honored — hence
        // the nullable round-trip rather than a `?? []`.
        //
        // Config.LoadCore migrates legacy tier/per-transform settings before they reach this seam,
        // so runtime resolution has exactly one source of truth.
        var plan = CompressionCatalog.Resolve(settings.TokenSaverStages);

        // The saver's global master kill switch is named for Claude for legacy reasons, but it
        // governs all providers. A Web UI terminal-tab child adds its process-local gate here;
        // the root process leaves that gate at its default true value.
        var tokenSaverEnabled = settings.ClaudeTokenSaverEnabled && tabTokenSaverEnabled;
        // Launch injection is a per-session decision. Once an OpenCode terminal has received the
        // local base URL, keep its authenticated relay alive until that terminal exits even if the
        // global launch toggle changes in the meantime. A later terminal still reads the new value.
        var openCodeProxyEnabled = settings.OpenCodeLlmProxyEnabled || openCodeProxyActive;

        return new LlmProxySettings(
            CodexLlmProxyEnabled: settings.CodexLlmProxyEnabled,
            CodexLlmProxyMode: CodexLlmProxySettings.NormalizeMode(settings.CodexLlmProxyMode),
            ClaudeLlmProxyEnabled: settings.ClaudeLlmProxyEnabled,
            OpenCodeLlmProxyEnabled: openCodeProxyEnabled,
            ClaudeTokenSaverEnabled: settings.ClaudeLlmProxyEnabled && tokenSaverEnabled,
            CodexTokenSaverEnabled: settings.CodexLlmProxyEnabled && tokenSaverEnabled,
            OpenCodeTokenSaverEnabled: openCodeProxyEnabled && tokenSaverEnabled,
            TokenSaverPlan: plan,
            TokenSaverCaptureEnabled: settings.TokenSaverCaptureEnabled)
        {
            OpenCodeLlmProxyLaunchEnabled = settings.OpenCodeLlmProxyEnabled
        };
    }
}
