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
    ILlmProxySessionState proxySessionState) : ILlmProxySettingsService
{
    public LlmProxySettings GetSettings()
    {
        return Resolve(
            Config.LoadFresh(),
            proxySessionState.OpenCodeProxyActive);
    }

    internal static LlmProxySettings Resolve(
        Settings settings,
        bool openCodeProxyActive = false)
    {
        ArgumentNullException.ThrowIfNull(settings);

        // The plan is the curated catalog set unless a hand-edited TokenSaverStageOverride is
        // present. Null means "no override" and resolves to the catalog defaults; an empty list is
        // a real "run nothing" choice and is honored — hence the nullable round-trip, not `?? []`.
        var plan = CompressionCatalog.Resolve(settings.TokenSaverStageOverride);

        // The saver is on/off per LLM. The Codex/OpenCode toggles are nullable so a pre-split
        // settings.json (which had only the master switch, named for Claude) keeps its old
        // behavior: absent keys inherit the master's value.
        var claudeSaver = settings.ClaudeTokenSaverEnabled;
        var codexSaver = settings.CodexTokenSaverEnabled ?? settings.ClaudeTokenSaverEnabled;
        var openCodeSaver = settings.OpenCodeTokenSaverEnabled ?? settings.ClaudeTokenSaverEnabled;
        // Launch injection is a per-session decision. Once an OpenCode terminal has received the
        // local base URL, keep its authenticated relay alive until that terminal exits even if the
        // global launch toggle changes in the meantime. A later terminal still reads the new value.
        var openCodeProxyEnabled = settings.OpenCodeLlmProxyEnabled || openCodeProxyActive;

        return new LlmProxySettings(
            CodexLlmProxyEnabled: settings.CodexLlmProxyEnabled,
            CodexLlmProxyMode: CodexLlmProxySettings.NormalizeMode(settings.CodexLlmProxyMode),
            ClaudeLlmProxyEnabled: settings.ClaudeLlmProxyEnabled,
            OpenCodeLlmProxyEnabled: openCodeProxyEnabled,
            ClaudeTokenSaverEnabled: settings.ClaudeLlmProxyEnabled && claudeSaver,
            CodexTokenSaverEnabled: settings.CodexLlmProxyEnabled && codexSaver,
            OpenCodeTokenSaverEnabled: openCodeProxyEnabled && openCodeSaver,
            TokenSaverPlan: plan,
            TokenSaverCaptureEnabled: settings.TokenSaverCaptureEnabled)
        {
            OpenCodeLlmProxyLaunchEnabled = settings.OpenCodeLlmProxyEnabled
        };
    }
}
